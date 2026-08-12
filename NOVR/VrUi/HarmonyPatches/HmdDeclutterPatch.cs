using HarmonyLib;
using NOVR.VrUi.Capture;
using UnityEngine;

namespace NOVR.VrUi.HarmonyPatches;

/// <summary>
/// Make the game's own HMD declutter work in VR by handing it the one input it
/// needs — the same shape as the design-eye swap, applied to a position instead
/// of a camera.
///
/// <c>HeadMountedDisplay.Update</c> hides each helmet readout when
/// <c>FlightHud.GetHUDCenter().position</c> comes within `HMD Hide Distance`
/// of it. On the flat screen both are pixels of the same space: "is this
/// readout overlapping the HUD". In VR the boresight symbol lives in the
/// captured HUD's screen space and the readouts in the visor's, so the
/// comparison is meaningless — this is the setting the 2026-08-11 dig found
/// genuinely broken by VR.
///
/// The prefix writes the boresight's position *in the visor's pixel space* —
/// where the nose actually is in the pilot's view — into the HUDCenter
/// transform for the duration of the call; the finalizer puts it back. The
/// hide logic itself runs unmodified, per widget, at the game's own setting.
/// </summary>
internal static class HmdDeclutterPatch
{
    [HarmonyPatch(typeof(HeadMountedDisplay), "Update")]
    private static class HeadMountedDisplayUpdatePatch
    {
        [HarmonyPrefix]
        private static void Prefix(out Vector3? __state)
        {
            __state = null;

            if (!HmdVisorBackend.IsVisorActive) return;
            var noseInVisor = HmdVisorBackend.NoseInVisorPixels();
            if (noseInVisor == null) return;

            var hudCenter = SceneSingleton<FlightHud>.i?.GetHUDCenter();
            if (hudCenter == null) return;

            __state = hudCenter.position;
            hudCenter.position = noseInVisor.Value;
        }

        [HarmonyFinalizer]
        private static void Finalizer(Vector3? __state)
        {
            if (__state == null) return;
            var hudCenter = SceneSingleton<FlightHud>.i?.GetHUDCenter();
            if (hudCenter != null) hudCenter.position = __state.Value;
        }
    }
}
