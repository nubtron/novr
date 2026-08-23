using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using NOVR.VrUi.Capture;
using UnityEngine;

namespace NOVR.VrUi.HarmonyPatches;

/// <summary>
/// While the game's HUD code runs, make it project from the fixed design eye
/// instead of the head — the same separation a real HUD has between the
/// symbol generator (fixed design eye) and the pilot's actual eye (handled by
/// the collimator; here, by the panel's distance).
///
/// Every world-referenced HUD symbol goes through the public field
/// <c>CameraStateManager.mainCamera</c> — <c>FlightHud.Update</c> (boresight,
/// velocity vector, ladder roll and scale), <c>CombatHUD.LateUpdate</c> (unit
/// markers via <c>HUDUnitMarker.UpdatePosition</c>, hit markers, edge pinning
/// via <c>HUDFunctions.PinToScreenEdge</c>, weapon displays) and
/// <c>CombatHUD.FixedUpdate</c> (weapon state). Because it is a plain field,
/// the patch is a swap, not a transpiler: the prefix stores the head camera
/// and writes the design eye in; a finalizer (which runs even if the original
/// throws) puts the head camera back. Nothing outside these calls ever sees
/// the swapped field.
///
/// The swap is inert unless <see cref="FlightHudCaptureBackend"/> is actively
/// capturing with conformal mode on — <c>ConformalProjectionCamera</c> is null
/// in every other state, including the whole life of a session with the
/// captured HUD disabled.
///
/// Known approximation: the behind-the-camera checks in <c>CombatHUD</c> and
/// <c>HUDUnitMarker</c> use <c>CameraStateManager.i.transform.forward</c> —
/// the head, not the field — so a marker can be culled by where the pilot
/// looks rather than by the airframe frustum. Wrong only past 90 degrees off
/// boresight, where the panel is out of view anyway.
///
/// <para><b>Two updaters do not read the field at all.</b> They call
/// <c>Camera.main</c>, which no swap can reach, so they projected through the
/// head while every other symbol beside them went through the design eye — see
/// <see cref="RedirectCameraMain"/>.</para>
/// </summary>
internal static class HudDesignEyePatches
{
    [HarmonyPatch(typeof(FlightHud), "Update")]
    private static class FlightHudUpdatePatch
    {
        [HarmonyPrefix]
        private static void Prefix(out Camera? __state) => Swap(out __state);

        [HarmonyFinalizer]
        private static void Finalizer(Camera? __state) => Restore(__state);
    }

    [HarmonyPatch(typeof(CombatHUD), "LateUpdate")]
    private static class CombatHudLateUpdatePatch
    {
        [HarmonyPrefix]
        private static void Prefix(out Camera? __state) => Swap(out __state);

        [HarmonyFinalizer]
        private static void Finalizer(Camera? __state) => Restore(__state);
    }

    [HarmonyPatch(typeof(CombatHUD), "FixedUpdate")]
    private static class CombatHudFixedUpdatePatch
    {
        [HarmonyPrefix]
        private static void Prefix(out Camera? __state) => Swap(out __state);

        [HarmonyFinalizer]
        private static void Finalizer(Camera? __state) => Restore(__state);
    }

    /// <summary>
    /// The landing symbology — the runway outline, the glideslope line and its
    /// aim point, the airbase marker and label — is world-referenced in exactly
    /// the same way as everything above, and was the one updater left off this
    /// list.
    ///
    /// <para>Measured mid-approach with the capture running: the field was
    /// being projected through <c>CameraStateManager.mainCamera</c>, which by
    /// then is still the game's original "Main Camera" — hundreds of metres
    /// from the aircraft and pointing somewhere else — while every other symbol
    /// on the panel went through the design eye. All four runway corners and
    /// both ends of the glideslope landed within ten pixels of the centre of a
    /// 2560x1440 screen, and the glideslope's length scale came out
    /// <i>negative</i> (-7.9), which is its aim point projecting behind the
    /// camera. A runway box too small to see and a glideslope drawn inside
    /// out.</para>
    /// </summary>
    [HarmonyPatch(typeof(AirbaseOverlay), "LateUpdate")]
    private static class AirbaseOverlayLateUpdatePatch
    {
        [HarmonyPrefix]
        private static void Prefix(out Camera? __state) => Swap(out __state);

        [HarmonyFinalizer]
        private static void Finalizer(Camera? __state) => Restore(__state);
    }

    /// <summary>
    /// The design eye while a swap is in effect, else null.
    ///
    /// <para>Read by <see cref="ViewLayerPatches"/>. The screen-space helpers
    /// it redirects — <c>HUDFunctions.PinToScreenEdge</c> above all — are
    /// shared, and are called from inside these swaps as well as from the view
    /// layer's own. Inside one of these the right reference is the design eye
    /// and the real screen; the view layer's virtual screen would put the
    /// airbase marker in a third coordinate system belonging to neither.</para>
    /// </summary>
    public static Camera? ActiveDesignEye { get; private set; }

    /// <summary>
    /// The camera the game's HUD code should project through: the design eye
    /// while a swap is in effect, and otherwise whatever <c>Camera.main</c>
    /// was going to be, so the redirect below is inert in every state this
    /// backend is not capturing in.
    /// </summary>
    public static Camera? ProjectionCamera() => ActiveDesignEye != null ? ActiveDesignEye : Camera.main;

    private static readonly MethodInfo CameraMainGetter =
        AccessTools.PropertyGetter(typeof(Camera), nameof(Camera.main));
    private static readonly MethodInfo ProjectionCameraGetter =
        AccessTools.Method(typeof(HudDesignEyePatches), nameof(ProjectionCamera));

    /// <summary>
    /// Rewrites <c>Camera.main</c> to <see cref="ProjectionCamera"/> inside one
    /// method.
    ///
    /// <para><b>Why this is needed at all.</b> The swap above works because
    /// every world-referenced HUD symbol reads the camera out of the public
    /// field <c>CameraStateManager.mainCamera</c> — that is what makes a field
    /// swap enough. Two of them do not: <c>HUDBombingState.UpdatePipperPosition</c>
    /// takes <c>Camera.main</c>, and so does <c>HUDTurretState.UpdateWeaponDisplay</c>
    /// when it refreshes each turret crosshair. <c>Camera.main</c> is a static
    /// that resolves by tag, so it goes on returning the head camera inside the
    /// swap, and the symbol is projected through the pilot's head onto a panel
    /// that is fixed to the airframe. The result is a CCIP pipper that slides
    /// across the HUD as the head turns, opposite to the head and unrelated to
    /// the aeroplane or the ground under it — which is the one thing a bombing
    /// pipper must never do.</para>
    ///
    /// <para>A transpiler rather than a re-implementation: the pipper's screen
    /// maths, its lead line and the turret crosshairs' own projection are the
    /// game's and should stay the game's. Only the camera they ask for is
    /// wrong. Nothing else in the process is touched — <c>Camera.main</c>
    /// everywhere else still means the head, which for everything else it
    /// should.</para>
    /// </summary>
    private static IEnumerable<CodeInstruction> RedirectCameraMain(
        IEnumerable<CodeInstruction> instructions, MethodBase original)
    {
        var replaced = 0;

        foreach (var instruction in instructions)
        {
            if (instruction.Calls(CameraMainGetter))
            {
                replaced++;
                yield return new CodeInstruction(OpCodes.Call, ProjectionCameraGetter)
                {
                    labels = instruction.labels,
                    blocks = instruction.blocks,
                };
                continue;
            }

            yield return instruction;
        }

        // A transpiler that matches nothing applies cleanly and fixes nothing,
        // and this one is the only thing standing between the pipper and the
        // head. Say so rather than leaving it to be rediscovered in a headset.
        if (replaced == 0)
        {
            Debug.LogError($"[NOVR] {nameof(RedirectCameraMain)} found no Camera.main call in " +
                           $"{original?.DeclaringType?.Name}.{original?.Name}: the symbol it draws will " +
                           "still be projected through the head. The game's HUD code has changed.");
        }
    }

    /// <summary>
    /// The CCIP pipper and its lead line to the velocity vector.
    /// </summary>
    [HarmonyPatch(typeof(global::HUDBombingState), "UpdatePipperPosition")]
    private static class BombingPipperCameraPatch
    {
        [HarmonyTranspiler]
        private static IEnumerable<CodeInstruction> Transpiler(
            IEnumerable<CodeInstruction> instructions, MethodBase original) =>
            RedirectCameraMain(instructions, original);
    }

    /// <summary>
    /// The turret crosshairs, which have the same defect for the same reason
    /// and were found beside it rather than reported.
    /// </summary>
    [HarmonyPatch(typeof(global::HUDTurretState), nameof(global::HUDTurretState.UpdateWeaponDisplay))]
    private static class TurretCrosshairCameraPatch
    {
        [HarmonyTranspiler]
        private static IEnumerable<CodeInstruction> Transpiler(
            IEnumerable<CodeInstruction> instructions, MethodBase original) =>
            RedirectCameraMain(instructions, original);
    }

    private static void Swap(out Camera? previous)
    {
        previous = null;

        var designEye = FlightHudCaptureBackend.AcquireDesignEyeForProjection();
        if (designEye == null) return;

        var manager = SceneSingleton<CameraStateManager>.i;
        if (manager == null || manager.mainCamera == null) return;

        previous = manager.mainCamera;
        manager.mainCamera = designEye;
        ActiveDesignEye = designEye;
    }

    private static void Restore(Camera? previous)
    {
        if (previous == null) return;

        ActiveDesignEye = null;
        var manager = SceneSingleton<CameraStateManager>.i;
        if (manager != null) manager.mainCamera = previous;
    }
}
