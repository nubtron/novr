using System;
using NOVR.VrUi.Capture;
using NOVR.VrUi.HarmonyPatches;
using UnityEngine;
using UnityEngine.UI;

namespace NOVR.VrUi.SpecialBehavior;

public class NOVRMainMenuBehavior : UIRenderedCanvasBehavior
{
    // Below the native root and the captured panel: either of those, when up,
    // is drawn over this canvas.
    private const int GazeAnchorPriority = 1;
    private const float QueueSweepInterval = 0.5f;

    // The captured-menu backend needs this canvas left exactly as the game
    // authored it: screen-space overlay, original layers, untouched transform.
    // Converting it here is what the capture path exists to avoid.
    protected override bool ShouldInitializeCanvas => !MenuCaptureBackend.Enabled;

    private void Start()
    {
        if (MenuCaptureBackend.Enabled) return;

        transform.localScale = new Vector3(0.003f, 0.003f, 0.003f);
        transform.localPosition = new Vector3(0f, 0f, 3f);
    }

    private void OnDisable() => MenuDrawOrderPatch.RestoreRetimedMaterials();

    private void OnDestroy() => MenuDrawOrderPatch.RestoreRetimedMaterials();

    private float _nextQueueSweep;

    private void Update()
    {
        SweepDrawOrder();

        // The patched game menu is the surface the cursor is driven against on
        // this path, so head-gaze amplification measures from its centre
        // rather than from a head pose captured when the cursor appeared.
        if (MenuCaptureBackend.Enabled) return;

        VrUiCursor.I?.SetGazeAnchorCenter(transform.position, GazeAnchorPriority);
    }

    /// <summary>
    /// Reading <c>materialForRendering</c> re-runs the material modifiers, and
    /// with them <see cref="MenuDrawOrderPatch"/>. That matters because the
    /// queues are restored whenever this canvas is disabled — including the
    /// single-frame bounce the behaviour patcher uses to force lifecycle
    /// callbacks — and nothing would otherwise re-apply them until a graphic
    /// happened to go dirty on its own.
    /// </summary>
    private void SweepDrawOrder()
    {
        if (MenuCaptureBackend.Enabled) return;
        if (Time.unscaledTime < _nextQueueSweep) return;
        _nextQueueSweep = Time.unscaledTime + QueueSweepInterval;

        foreach (var graphic in GetComponentsInChildren<MaskableGraphic>(true))
        {
            if (graphic == null) continue;
            _ = graphic.materialForRendering;
        }
    }
}
