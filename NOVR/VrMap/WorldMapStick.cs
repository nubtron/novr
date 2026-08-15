using UnityEngine;
using UnityEngine.InputSystem.Controls;
using UnityEngine.InputSystem.XR;
using UnityEngine.XR;
using XRCommonUsages = UnityEngine.XR.CommonUsages;

namespace NOVR.VrMap;

/// <summary>
/// The thumbsticks on the VR controllers, which nothing else in the game or the
/// mod reads.
///
/// <para><b>Why this exists at all.</b> The map was given the aeroplane's Pitch,
/// Roll and Yaw so that whatever the pilot flies with would move it — HOTAS,
/// gamepad, keyboard, anything, without being told about. That is right for the
/// stick in your hand and it misses the obvious one: a pilot in a headset has two
/// thumbsticks nobody has bound to anything, and reaching for the flight stick to
/// pan a map you are looking down at is the wrong gesture. A flight reported it
/// as simply "I still couldn't move around the map with the controller", and the
/// reason is that nothing in this mod had ever read a thumbstick.</para>
///
/// <para><b>Why the legacy API is tried first, which is backwards from
/// everywhere else in the mod.</b> On the rig this was reported from, the Input
/// System never manages to build the controller at all — the player log carries
/// "Could not create a device for 'Oculus Oculus Touch Controller OpenXR
/// (XRInputV1)' ... Layout has not been set on control 'haptic'" every time the
/// controllers wake up, three times in one session. So <c>XRController.leftHand</c>
/// is null there and anything written against it is dead code on a machine that
/// has working controllers by every other measure: the poses, the trigger and the
/// hand models all come through <c>InputDevices</c> and are fine. Both are asked
/// here, the one with an actual deflection wins, and which one answered is
/// logged once — because "the stick does nothing" has two very different causes
/// and they are not distinguishable from inside the headset.</para>
/// </summary>
internal static class WorldMapStick
{
    /// <summary>
    /// Below this a stick is treated as centred. Thumbsticks rest a little off
    /// zero and a map that drifts on its own is worse than one that needs a
    /// firmer push.
    /// </summary>
    private const float Deadzone = 0.15f;

    private static bool _reported;
    private static string _source = "nothing";

    public static Vector2 Left => Read(XRNode.LeftHand);
    public static Vector2 Right => Read(XRNode.RightHand);

    /// <summary>Which API the sticks are actually coming through, for the log.</summary>
    public static string Source => _source;

    /// <summary>True once either stick has been seen to move, so callers can say so.</summary>
    public static bool Seen => _reported;

    private static Vector2 Read(XRNode node)
    {
        var value = Vector2.zero;
        var source = "nothing";

        var controller = node == XRNode.RightHand ? XRController.rightHand : XRController.leftHand;
        if (controller != null)
        {
            // Unity's XR controller layout calls it 'thumbstick'; the generic
            // profile layouts call the same control 'primary2DAxis'. Neither name
            // is guaranteed, so both are asked for.
            var control = controller.TryGetChildControl<Vector2Control>("thumbstick")
                          ?? controller.TryGetChildControl<Vector2Control>("primary2DAxis");
            if (control != null)
            {
                value = control.ReadValue();
                source = "the Input System";
            }
        }

        if (value.sqrMagnitude < Deadzone * Deadzone)
        {
            var legacy = InputDevices.GetDeviceAtXRNode(node);
            if (legacy.isValid &&
                legacy.TryGetFeatureValue(XRCommonUsages.primary2DAxis, out var legacyValue) &&
                legacyValue.sqrMagnitude >= Deadzone * Deadzone)
            {
                value = legacyValue;
                source = "InputDevices";
            }
        }

        if (value.sqrMagnitude < Deadzone * Deadzone) return Vector2.zero;

        if (!_reported)
        {
            _reported = true;
            _source = source;
            Debug.Log($"[NOVR] World map: the {(node == XRNode.LeftHand ? "left" : "right")} thumbstick " +
                      $"is being read through {source}.");
        }

        // Rescale so the first movement past the deadzone is slow rather than a
        // jump to 15% of full speed.
        var magnitude = value.magnitude;
        return value / magnitude * Mathf.Clamp01((magnitude - Deadzone) / (1f - Deadzone));
    }
}
