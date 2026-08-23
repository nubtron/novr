using UnityEngine;

namespace NOVR;

/// <summary>
/// Turns the head for an unattended run, so the harness can look left and right
/// instead of only ever dumping straight ahead.
///
/// The yaw is applied at NOVR's own head-pose source (<see cref="NOVRHeadsetData"/>),
/// which is the single value the whole mod derives the view from: the tracked
/// camera, the HUD reference direction, the pitch-ladder window, the gaze
/// cursor. Rotating the camera transform after the fact would move the picture
/// while leaving all of those believing the head never moved, and would test a
/// situation that cannot occur. Rotating the source is a head turn as far as
/// every consumer is concerned.
///
/// What it does not exercise is the runtime → InputTracking path: the pose the
/// OpenXR runtime reports is untouched, so this cannot catch a bug in how NOVR
/// reads tracking. That is a real limit, and it is the reason the first attempt
/// went through the mock runtime's own test API (mock_api.dll,
/// MockRuntime_SetView) instead. That API is inert here — mock_api learns where
/// the runtime lives only through the hook the MockRuntime *feature* installs at
/// instance creation, and NOVR loads the runtime straight from XR_RUNTIME_JSON
/// without that feature. The calls succeed and change nothing: baseline poses
/// read back fine, and every dump in a five-angle sweep came out at 0°. If a
/// tracking-path test is ever needed, that hook is what has to be installed
/// first.
/// </summary>
public static class HarnessViewPose
{
    public static float CurrentYaw { get; private set; }

    /// <summary>
    /// Point the head <paramref name="degrees"/> to the right of where the
    /// pilot is looking; negative looks left.
    /// </summary>
    public static void SetYaw(float degrees)
    {
        CurrentYaw = degrees;
        NOVRHeadsetData.HarnessYawOffset = degrees;
    }

    /// <summary>
    /// Where the head actually ended up, for the dump and the log to record. A
    /// yaw that was requested is not a yaw that happened — the first
    /// implementation of this class requested five different angles and
    /// produced five identical frames.
    /// </summary>
    public static float MeasuredYaw()
    {
        return Mathf.DeltaAngle(0f, NOVRHeadsetData.Rotation.eulerAngles.y);
    }
}
