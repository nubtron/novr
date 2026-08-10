using System;
using NOVR.VrUi.Capture;
using UnityEngine;
using UnityEngine.UI;

namespace NOVR.VrUi.SpecialBehavior;

public class NOVRMainMenuBehavior : UIRenderedCanvasBehavior
{
    // Below the native root and the captured panel: either of those, when up,
    // is drawn over this canvas.
    private const int GazeAnchorPriority = 1;

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

    private void Update()
    {
        // The patched game menu is the surface the cursor is driven against on
        // this path, so head-gaze amplification measures from its centre
        // rather than from a head pose captured when the cursor appeared.
        if (MenuCaptureBackend.Enabled) return;

        VrUiCursor.I?.SetGazeAnchorCenter(transform.position, GazeAnchorPriority);
    }
}
