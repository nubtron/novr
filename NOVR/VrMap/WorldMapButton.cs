using UnityEngine.InputSystem.Controls;
using UnityEngine.InputSystem.XR;
using UnityEngine.XR;
using XRCommonUsages = UnityEngine.XR.CommonUsages;

namespace NOVR.VrMap;

/// <summary>
/// A button on a VR controller, as something a setting can name.
///
/// <para>All of them are free. The game is not a VR game: it has no controller
/// bindings of its own, and the controllers exist only inside this mod. So
/// binding one costs nothing and nothing the game does can shadow it — which is
/// the answer to "what keybind can we reuse", namely none of them, because there
/// is a whole controller nobody is using.</para>
/// </summary>
public enum WorldMapButton
{
    None,

    /// <summary>Left hand, lower face button — X on a Touch controller.</summary>
    LeftPrimary,

    /// <summary>Left hand, upper face button — Y on a Touch controller.</summary>
    LeftSecondary,

    /// <summary>Right hand, lower face button — A on a Touch controller.</summary>
    RightPrimary,

    /// <summary>Right hand, upper face button — B on a Touch controller.</summary>
    RightSecondary,

    /// <summary>Left hand, thumbstick pressed in.</summary>
    LeftStick,

    /// <summary>Right hand, thumbstick pressed in.</summary>
    RightStick
}

/// <summary>
/// Reads those buttons, through the same two APIs <see cref="VrUi.MotionControllerPose"/>
/// uses and in the same order: the Input System's XR controller first, because
/// that is how Unity's OpenXR backend exposes an interaction profile, and the
/// legacy device API second.
/// </summary>
internal static class WorldMapButtons
{
    private static bool _wasDown;

    /// <summary>True on the frame the bound button goes down, once per press.</summary>
    public static bool Pressed()
    {
        var button = VrMapConfig.ToggleButton != null
            ? VrMapConfig.ToggleButton.Value
            : WorldMapButton.None;

        if (button == WorldMapButton.None)
        {
            _wasDown = false;
            return false;
        }

        var down = Down(button);
        var pressed = down && !_wasDown;
        _wasDown = down;
        return pressed;
    }

    private static bool Down(WorldMapButton button)
    {
        var hand = button switch
        {
            WorldMapButton.LeftPrimary or WorldMapButton.LeftSecondary or WorldMapButton.LeftStick
                => XRNode.LeftHand,
            _ => XRNode.RightHand
        };

        var control = button switch
        {
            WorldMapButton.LeftPrimary or WorldMapButton.RightPrimary => "primaryButton",
            WorldMapButton.LeftSecondary or WorldMapButton.RightSecondary => "secondaryButton",
            _ => "thumbstickClicked"
        };

        var controller = hand == XRNode.RightHand ? XRController.rightHand : XRController.leftHand;
        if (controller != null)
        {
            var read = controller.TryGetChildControl<ButtonControl>(control);
            if (read != null) return read.isPressed;
        }

        var legacy = InputDevices.GetDeviceAtXRNode(hand);
        if (!legacy.isValid) return false;

        var usage = button switch
        {
            WorldMapButton.LeftPrimary or WorldMapButton.RightPrimary => XRCommonUsages.primaryButton,
            WorldMapButton.LeftSecondary or WorldMapButton.RightSecondary => XRCommonUsages.secondaryButton,
            _ => XRCommonUsages.primary2DAxisClick
        };

        return legacy.TryGetFeatureValue(usage, out var value) && value;
    }
}
