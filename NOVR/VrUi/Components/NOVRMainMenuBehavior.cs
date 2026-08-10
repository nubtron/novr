using System;
using UnityEngine;
using UnityEngine.UI;

namespace NOVR.VrUi.SpecialBehavior;

public class NOVRMainMenuBehavior : UIRenderedCanvasBehavior
{
    // Below the native root, which is drawn over this canvas when it is up.
    private const int GazeAnchorPriority = 1;

    private void Start()
    {
        transform.localScale = new Vector3(0.003f, 0.003f, 0.003f);
        transform.localPosition = new Vector3(0f, 0f, 3f);
    }

    private void Update()
    {
        // The game's menu canvas is the surface the cursor is driven against,
        // so head-gaze amplification measures from its centre rather than from
        // a head pose captured when the cursor appeared.
        VrUiCursor.I?.SetGazeAnchorCenter(transform.position, GazeAnchorPriority);
    }
}
