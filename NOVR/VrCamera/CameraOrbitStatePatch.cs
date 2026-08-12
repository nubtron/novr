using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace NOVR.VrCamera;

/// <summary>
/// The spectator camera — what you get after death, when following a wingman,
/// or when spectating anyone else's aircraft.
///
/// <c>CameraOrbitState.CameraMotion</c> trails the followed unit: it keeps
/// <c>cameraPivot</c> on the unit, turns the pivot to the unit's smoothed
/// ground track (plus the player's pan/tilt), places the camera one unit-radius
/// ring behind it, pulls it in on a linecast so terrain cannot swallow it, and
/// looks back at the unit.
///
/// It used to be replaced wholesale here — the camera was pinned to the pivot,
/// i.e. to the unit's own origin. That is inside the aircraft: measured on a
/// spectated Trainer, camera-to-unit distance 0.00 m against a 6.60 m unit
/// radius, and the frame dump is a view of the fuselage interior. The intent
/// was comfort ("remove camera orbiting"), and what it removed along with the
/// orbiting was the entire viewing distance.
///
/// It also did not deliver the comfort. <c>cameraPivot</c> is parented to the
/// followed rigidbody, so with the game's own rotation update skipped the pivot
/// inherits the aircraft's attitude: only the roll it happened to have when the
/// state was entered is cancelled out (measured: pivot roll ≈ 0° while the unit
/// held a steady 12.5° bank, because the state was entered at that bank). Every
/// change in bank after that rolls the view.
///
/// So the game's own motion runs again, and VR takes the one thing it must:
/// the parent's rotation is flattened to yaw. The camera keeps its own pitch
/// out of the equation because it is the headset's parent — a pitched parent
/// turns head yaw into horizon roll, which is the sharpest edge of VR sickness.
/// Level, the horizon stays the horizon however the pilot's head moves; the
/// aircraft sits a little below the middle of the view and pan/tilt still orbit
/// it, because those rotations are commanded rather than imposed.
/// </summary>
internal static class CameraOrbitStatePatch
{
    // The game enters the state at 20° above the target. Level-parented, that
    // is 20° of permanent look-down; 8° puts the aircraft near the middle of a
    // level view. A judgement call, not a measurement — "Tilt View" still moves
    // it wherever the player wants from there.
    private const float VrEntryTilt = 8f;

    [HarmonyPatch(typeof(CameraOrbitState), "CameraMotion")]
    private static class CameraMotionPatch
    {
        [HarmonyPostfix]
        private static void Postfix(CameraStateManager cam)
        {
            if (cam == null) return;

            var forward = cam.transform.forward;
            forward.y = 0f;

            if (forward.sqrMagnitude < 1e-6f)
            {
                // Looking straight up or down: the flattened forward carries no
                // heading, so take it from the camera's up axis, which lies in
                // the horizontal plane exactly then.
                forward = cam.transform.forward.y > 0f ? -cam.transform.up : cam.transform.up;
                forward.y = 0f;
                if (forward.sqrMagnitude < 1e-6f) return;
            }

            cam.transform.rotation = Quaternion.LookRotation(forward, Vector3.up);
        }
    }

    [HarmonyPatch(typeof(CameraOrbitState), "EnterState")]
    private static class EnterStatePatch
    {
        private static readonly FieldInfo? TiltViewField =
            AccessTools.Field(typeof(CameraOrbitState), "tiltView");

        [HarmonyPostfix]
        private static void Postfix(CameraOrbitState __instance)
        {
            TiltViewField?.SetValue(__instance, VrEntryTilt);
        }
    }
}
