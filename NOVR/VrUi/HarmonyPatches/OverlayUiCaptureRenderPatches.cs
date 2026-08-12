using System;
using HarmonyLib;
using NOVR.VrUi.Capture;
using UnityEngine.Rendering.Universal;

namespace NOVR.VrUi.HarmonyPatches;

/// <summary>
/// The two render-pipeline hooks the captured-menu backend needs.
///
/// 1. <see cref="SetupPatch"/> enqueues <see cref="OverlayUiCapturePass"/> on
///    the capture camera's renderer. A BepInEx plugin cannot edit the URP
///    asset's renderer feature list, so the pass is injected where the renderer
///    builds its queue for the frame.
/// 2. <see cref="RendersOverlayUiPatch"/> stops URP drawing the same canvases
///    into the eye buffers. Both of URP's overlay-UI passes are gated on
///    <c>CameraData.rendersOverlayUI</c>, so denying it there leaves the
///    capture pass as the only thing that draws them — otherwise the menu
///    would appear twice: once on the panel and once smeared flat across both
///    eyes.
/// </summary>
internal static class OverlayUiCaptureRenderPatches
{
    private static readonly OverlayUiCapturePass CapturePass = new();

    private static Action<ScriptableRenderer, ScriptableRenderPass>? _enqueuePass;

    private static Action<ScriptableRenderer, ScriptableRenderPass>? EnqueuePass =>
        _enqueuePass ??= BuildEnqueueDelegate();

    private static Action<ScriptableRenderer, ScriptableRenderPass>? BuildEnqueueDelegate()
    {
        try
        {
            return AccessTools.MethodDelegate<Action<ScriptableRenderer, ScriptableRenderPass>>(
                AccessTools.Method(typeof(ScriptableRenderer), "EnqueuePass"));
        }
        catch (Exception e)
        {
            MenuCaptureBackend.ReportRenderHookFailure($"EnqueuePass unavailable: {e.Message}");
            return null;
        }
    }

    [HarmonyPatch(typeof(UniversalRenderer), nameof(UniversalRenderer.Setup))]
    private static class SetupPatch
    {
        [HarmonyPostfix]
        private static void Postfix(ScriptableRenderer __instance, ref RenderingData renderingData)
        {
            var camera = renderingData.cameraData.camera;
            var isMenuCapture = MenuCaptureBackend.IsCaptureCamera(camera);
            if (!isMenuCapture && !FlightHudCaptureBackend.IsCaptureCamera(camera)) return;

            var enqueue = EnqueuePass;
            if (enqueue == null) return;

            enqueue(__instance, CapturePass);
            if (isMenuCapture) MenuCaptureBackend.NotifyCapturePassEnqueued();
        }
    }

    [HarmonyPatch(typeof(CameraData), nameof(CameraData.rendersOverlayUI), MethodType.Getter)]
    private static class RendersOverlayUiPatch
    {
        [HarmonyPostfix]
        private static void Postfix(ref bool __result)
        {
            if (!__result) return;
            // Either backend: whichever one is capturing has taken ownership of
            // the overlay canvases, and URP must stop drawing them into the eye
            // buffers or they appear twice — once on the panel, once smeared
            // flat across both eyes.
            if (!MenuCaptureBackend.SuppressesScreenOverlayUi &&
                !FlightHudCaptureBackend.IsActive) return;

            __result = false;
        }
    }
}
