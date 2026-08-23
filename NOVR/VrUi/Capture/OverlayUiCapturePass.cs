using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace NOVR.VrUi.Capture;

/// <summary>
/// Draws the game's ScreenSpaceOverlay canvases into whatever target the
/// capture camera is rendering to.
///
/// <see cref="ScriptableRenderContext.DrawUIOverlay"/> is the engine's own
/// overlay-UI entry point — the same call URP makes in its
/// <c>DrawScreenSpaceUIPass</c> — so the canvases render through the real
/// overlay path: strict hierarchy draw order, render queue ignored, local z
/// ignored, layers and cameras irrelevant, stencil masks behaving exactly as
/// they do on a flat screen. That is the whole point of this backend: rather
/// than converting canvases to world space and then repairing the differences
/// one by one, it never leaves the mode the UI was authored for.
/// </summary>
internal class OverlayUiCapturePass : ScriptableRenderPass
{
    public OverlayUiCapturePass()
    {
        profilingSampler = new ProfilingSampler(nameof(OverlayUiCapturePass));
        renderPassEvent = RenderPassEvent.AfterRendering;
    }

    public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
    {
        // The overlay draw goes straight to the context rather than through a
        // command buffer, so it has to be ordered after everything already
        // queued for this camera — the target binding and the clear. URP's
        // ScriptableRenderer submits its buffer immediately before invoking a
        // pass, so by here that has happened and drawing directly is correct.
        context.DrawUIOverlay(renderingData.cameraData.camera);
    }
}
