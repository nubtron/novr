using System.ComponentModel;
using BepInEx.Configuration;
using UnityEngine;

namespace NOVR;

public class ModConfiguration
{
    public static ModConfiguration Instance;
    

    public readonly ConfigFile Config;
    public readonly ConfigEntry<float> TargetDesignatorOvershoot;
    public readonly ConfigEntry<float> CursorSizeMultiplier;
    public readonly ConfigEntry<float> VrHudScale;
    public readonly ConfigEntry<float> HudElementScale;
    public readonly ConfigEntry<float> HudLineThickness;
    public readonly ConfigEntry<float> PitchLadderWidth;
    public readonly ConfigEntry<float> PitchLadderRange;
    public readonly ConfigEntry<float> ColorContrast;
    public readonly ConfigEntry<float> ColorSaturation;
    public readonly ConfigEntry<float> ColorGamma;
    public readonly ConfigEntry<KeyCode> ColorGammaDecreaseShortcut;
    public readonly ConfigEntry<KeyCode> ColorGammaIncreaseShortcut;
    public readonly ConfigEntry<string> CursorInputSource;
    public readonly ConfigEntry<float> CursorControllerSmoothing;
    public readonly ConfigEntry<bool> ShowMotionControllers;
    public readonly ConfigEntry<bool> ShowControllerLaser;
    public readonly ConfigEntry<float> ControllerIdleTimeout;
    public readonly ConfigEntry<bool> EnableNativeMenuUi;
    public readonly ConfigEntry<float> NativeMenuScale;
    public readonly ConfigEntry<float> NativeMenuDistance;
    public readonly ConfigEntry<float> NativeMenuHeightOffset;

    public ModConfiguration(ConfigFile config)
    {
        Instance = this;

        Config = config;
        TargetDesignatorOvershoot = config.Bind(
            "General",
            "Target Designator Overshoot",
            1.2f,
            "How much the target designator should multiply rotation to make for easier high off boresight target designation. Set to 1.0 to disable");

        CursorSizeMultiplier = config.Bind(
            "General",
            "Cursor Size Multiplier",
            2.0f,
            new ConfigDescription(
                "Visual size of the VR cursor. Values from 1.0 to 3.0 are supported.",
                new AcceptableValueRange<float>(1.0f, 3.0f)));

        VrHudScale = config.Bind(
            "General",
            "VR HUD Scale",
            0.8f,
            new ConfigDescription(
                "Scale of the whole VR flight HUD, including the head-locked HMD and cockpit HUD centers. This sets how far the HUD elements sit from the view center, so raising it spreads them toward the edge of comfortable head movement. To make the symbols themselves bigger without spreading them out, use HUD Element Scale instead. Values from 0.25 to 1.5 are supported.",
                new AcceptableValueRange<float>(0.25f, 1.5f)));

        HudElementScale = config.Bind(
            "General",
            "HUD Element Scale",
            1.0f,
            new ConfigDescription(
                "Size of the individual HUD symbols (numbers, icons, markers) without moving them. VR HUD Scale alone cannot solve legibility: it scales element size and their distance from the view center together, so a value large enough to read pushes the outer elements past comfortable head movement, and a value tight enough to see leaves the symbols too small. This scales each element in place instead, so the HUD envelope stays where VR HUD Scale puts it. Values from 0.5 to 3.0 are supported.",
                new AcceptableValueRange<float>(0.5f, 3.0f)));

        HudLineThickness = config.Bind(
            "General",
            "HUD Line Thickness",
            1.5f,
            new ConfigDescription(
                "Thickness multiplier for VR HUD lines: the pitch ladder (dashes, tick marks, labels) and thin line elements of the main HUD such as borders, brackets, tapes, and the waterline. 1.0 is the game's original line width.",
                new AcceptableValueRange<float>(0.5f, 3.0f)));

        PitchLadderWidth = config.Bind(
            "General",
            "Pitch Ladder Width",
            0.9f,
            new ConfigDescription(
                "Fraction of the pitch ladder's original width kept, centered on the view. Values below about 0.5 crop the ladder lines toward the center into short bars and remove the pitch numbers; 1.0 keeps the full original width with numbers.",
                new AcceptableValueRange<float>(0.15f, 1.0f)));

        PitchLadderRange = config.Bind(
            "General",
            "Pitch Ladder Range",
            15f,
            new ConfigDescription(
                "Minimum half-angle in degrees of pitch lines always visible around dead ahead (the HUD center, i.e. the aircraft nose — not the view direction). Tilting your head up or down reveals additional pitch lines in that direction, up to 90 degrees; looking sideways adds none. Lines stay horizon-referenced, so the horizon line always points at the true horizon.",
                new AcceptableValueRange<float>(5f, 90f)));

        ColorContrast = config.Bind(
            "Display",
            "Color Contrast",
            15f,
            new ConfigDescription(
                "Contrast boost applied to the final image to counteract washed-out colors (e.g. a headset streamer's gamma/color mapping). 0 disables.",
                new AcceptableValueRange<float>(-100f, 100f)));

        ColorSaturation = config.Bind(
            "Display",
            "Color Saturation",
            10f,
            new ConfigDescription(
                "Saturation boost applied to the final image. 0 disables.",
                new AcceptableValueRange<float>(-100f, 100f)));

        ColorGamma = config.Bind(
            "Display",
            "Color Gamma",
            0f,
            new ConfigDescription(
                "Gamma adjustment applied to the final image. Negative darkens the midtones, positive lifts them; highlights and blacks move far less than with contrast, which is what makes it the right control for a headset streamer's gamma curve. 0 disables. Adjustable in flight with the two shortcuts below, which write the value back here, so you can tune it in the headset and keep what you picked.",
                new AcceptableValueRange<float>(-1f, 1f)));

        // Live tuning is the point of these: the value that cancels the
        // streamer's curve cannot be judged from a desktop mirror, and quitting
        // to edit a config file loses the comparison you were making.
        ColorGammaDecreaseShortcut = config.Bind(
            "Input",
            "Color Gamma Decrease Shortcut",
            KeyCode.LeftBracket,
            "Keyboard shortcut to darken the midtones by one step (0.05). Rebind if your keyboard layout has no bracket keys.");

        ColorGammaIncreaseShortcut = config.Bind(
            "Input",
            "Color Gamma Increase Shortcut",
            KeyCode.RightBracket,
            "Keyboard shortcut to lift the midtones by one step (0.05). Rebind if your keyboard layout has no bracket keys.");
        CursorInputSource = config.Bind(
            "General",
            "Cursor Input Source",
            "Mouse",
            "Controls the VR cursor: 'Mouse' uses the desktop mouse, 'Right Hand' or 'Left Hand' points it with an XR motion controller (trigger = click).");

        CursorControllerSmoothing = config.Bind(
            "General",
            "Cursor Controller Smoothing",
            0.3f,
            new ConfigDescription(
                "How quickly the cursor tracks the controller ray. Higher = snappier, lower = smoother.",
                new AcceptableValueRange<float>(0.05f, 0.95f)));

        ShowMotionControllers = config.Bind(
            "General",
            "Show Motion Controllers",
            true,
            "Show a simple controller model at each tracked hand in VR.");

        ShowControllerLaser = config.Bind(
            "General",
            "Show Controller Laser",
            true,
            "Show a laser pointer from the controller to the cursor.");

        ControllerIdleTimeout = config.Bind(
            "General",
            "Controller Idle Timeout",
            4f,
            new ConfigDescription(
                "Hide the controller model after this many seconds without movement (e.g. when put down). 0 disables.",
                new AcceptableValueRange<float>(0f, 60f)));

        EnableNativeMenuUi = config.Bind(
            "Experimental",
            "Enable Native Menu UI",
            true,
            "Use NOVR's native VR menu UI for non-flight menus. Disable to fall back to the existing patched game UI.");

        NativeMenuScale = config.Bind(
            "Experimental",
            "Native Menu Scale",
            1.25f,
            "Size multiplier for NOVR's native VR menu UI. Values from 0.75 to 2.0 are supported.");

        NativeMenuDistance = config.Bind(
            "Experimental",
            "Native Menu Distance",
            3.0f,
            "Distance in meters from the headset when NOVR's native VR menu UI is opened or recentered. Values from 1.5 to 6.0 are supported.");

        NativeMenuHeightOffset = config.Bind(
            "Experimental",
            "Native Menu Height Offset",
            0.0f,
            "Vertical offset in meters applied when NOVR's native VR menu UI is opened or recentered. Values from -0.25 to 1.0 are supported.");
    }
}
