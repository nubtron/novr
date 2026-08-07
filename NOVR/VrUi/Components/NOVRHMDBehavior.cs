using UnityEngine;

namespace NOVR.VrUi.SpecialBehavior;

public class NOVRHMDBehavior : UIRenderedCanvasBehavior
{
    private const float HudDistance = 1000f;

    private void Update()
    {
        // Keep the head-locked HMD canvas centered on the pilot's view. The
        // canvas itself is scaled from NOVRFlightHudBehavior so that both the
        // HMD and cockpit HUD centers scale together. Per-element positions are
        // left to the game's own HUD layout settings (they were previously
        // overwritten here every frame, which is why those settings had no
        // effect in VR).
        var hudReference = APIBus.CockpitHudReference.transform;
        transform.SetPositionAndRotation(
            hudReference.position + hudReference.forward * HudDistance,
            hudReference.rotation);
    }
}
