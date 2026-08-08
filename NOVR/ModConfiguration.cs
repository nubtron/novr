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
            0.6f,
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
            15f,
            new ConfigDescription(
                "Minimum half-angle in degrees of pitch lines always visible around dead ahead (the HUD center, i.e. the aircraft nose — not the view direction). Tilting your head up or down reveals additional pitch lines in that direction, up to 90 degrees; looking sideways adds none. Lines stay horizon-referenced, so the horizon line always points at the true horizon.",
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
