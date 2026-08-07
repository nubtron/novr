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
    public readonly ConfigEntry<float> HudLineThickness;
    public readonly ConfigEntry<float> PitchLadderWidth;
    public readonly ConfigEntry<float> PitchLadderRange;
    public readonly ConfigEntry<bool> EnableNativeMenuUi;
    public readonly ConfigEntry<float> NativeMenuScale;
    public readonly ConfigEntry<float> NativeMenuDistance;
    public readonly ConfigEntry<float> NativeMenuHeightOffset;

    public ModConfiguration(ConfigFile config)
    {
        Instance = this;

        Config = config;

        // Anything marked [ConfigSection] binds itself, so a feature that owns
        // its settings in its own file needs no line in this constructor.
        //
        // Deliberately the first thing the constructor does rather than the
        // last: every branch that adds a setting appends to the end of this
        // method, so an extension point placed there would be in a permanent
        // three-way tug of war with the very thing it exists to prevent.
        ConfigSections.BindAll(config);
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
            0.5f,
            new ConfigDescription(
                "Scale of the VR flight HUD, including the head-locked HMD and cockpit HUD centers. Values from 0.25 to 1.5 are supported.",
                new AcceptableValueRange<float>(0.25f, 1.5f)));

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
            30f,
            new ConfigDescription(
                "Degrees of pitch around the horizon that the pitch ladder covers. Lower values crop the ladder to a window near the center of the view instead of spanning it top to bottom; 90 shows the full ladder.",
                new AcceptableValueRange<float>(5f, 90f)));

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
