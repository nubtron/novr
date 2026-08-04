using System.ComponentModel;
using BepInEx.Configuration;
using UnityEngine;
using UnityEngine.InputSystem;

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
    public readonly ConfigEntry<string> CursorInputSource;
    public readonly ConfigEntry<bool> HeadGazeCursor;
    public readonly ConfigEntry<float> HeadGazeMultiplier;
    public readonly ConfigEntry<Key> HeadGazeClickKey;
    public readonly ConfigEntry<float> CursorControllerSmoothing;
    public readonly ConfigEntry<bool> ShowMotionControllers;
    public readonly ConfigEntry<bool> ShowControllerLaser;
    public readonly ConfigEntry<float> ControllerIdleTimeout;
    public readonly ConfigEntry<bool> EnableNativeMenuUi;
    public readonly ConfigEntry<float> NativeMenuScale;
    public readonly ConfigEntry<float> NativeMenuDistance;
    public readonly ConfigEntry<float> NativeMenuHeightOffset;
    public readonly ConfigEntry<bool> ShowNativeUiButton;
    public readonly ConfigEntry<float> CapturedMenuDistance;
    public readonly ConfigEntry<float> CapturedMenuWidth;
    public readonly ConfigEntry<bool> EnableFrameDumps;
    public readonly ConfigEntry<bool> DisableVrMod;
    public readonly ConfigEntry<bool> RenderDocCaptureOnDump;
    public readonly ConfigEntry<bool> AutoStartMission;
    public readonly ConfigEntry<string> AutoStartMissionName;
    public readonly ConfigEntry<int> AutoDumpCount;
    public readonly ConfigEntry<float> AutoDumpDelay;
    public readonly ConfigEntry<float> ZoomSpeed;
    public readonly ConfigEntry<float> MaximumZoom;
    public readonly ConfigEntry<bool> InstantZoomOut;

    public ModConfiguration(ConfigFile config)
    {
        Instance = this;

        Config = config;

        DisableVrMod = config.Bind(
            "General",
            "Disable VR Mod",
            false,
            "Fully disable the VR mod: no patches are applied and XR is never started, so the game runs exactly as vanilla. Also enabled by launching the game with --no-vr (e.g. Steam Launch Options), or overridden back on with --vr. Takes effect on the next launch.");

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

        CursorInputSource = config.Bind(
            "General",
            "Cursor Input Source",
            "Mouse",
            "Controls the VR cursor: 'Mouse' uses the desktop mouse, 'Right Hand' or 'Left Hand' points it with an XR motion controller (trigger = click). Ignored while Head Gaze Cursor is enabled.");

        HeadGazeCursor = config.Bind(
            "General",
            "Head Gaze Cursor",
            true,
            "Keep the VR cursor centered in your view, following where your head looks; the controller trigger clicks. While enabled, the mouse and motion controller cursor modes are disabled.");

        HeadGazeMultiplier = config.Bind(
            "General",
            "Head Gaze Multiplier",
            2.0f,
            new ConfigDescription(
                "How much the head-gaze cursor moves relative to your head turn. 1.0 keeps the cursor at the exact center of your view; higher values amplify head movement so you reach the edges of menus with less neck craning.",
                new AcceptableValueRange<float>(0.5f, 3.0f)));

        HeadGazeClickKey = config.Bind(
            "General",
            "Head Gaze Click Key",
            Key.LeftAlt,
            "Keyboard key that clicks while held in head-gaze mode (the controller trigger and the game's Fire action also click). Useful when the headset's controllers are not tracked.");

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

        ShowNativeUiButton = config.Bind(
            "Experimental",
            "Show Native UI Button",
            true,
            "Show the 'VR UI ON' button in the headset while the game's own menus are in use. It switches back to NOVR's native VR menu UI. Turn it off to keep it out of the view; the native UI can still be re-enabled with Enable Native Menu UI in this file.");

        CapturedMenuDistance = config.Bind(
            "Experimental",
            "Captured Menu Distance",
            2.5f,
            "Distance in meters from the headset at which the captured menu panel is placed. Values from 1.0 to 6.0 are supported.");

        CapturedMenuWidth = config.Bind(
            "Experimental",
            "Captured Menu Width",
            4.0f,
            "Width in meters of the captured menu panel. Values from 0.5 to 8.0 are supported.");

        // [Debug] drives the offline verification harness (tools/). Every key
        // here is off by default, and a normal play session has to behave as if
        // the section did not exist: no hotkey doing anything surprising, no
        // capture firing, no mission starting by itself. The harness turns on
        // what it needs for the duration of a run and puts it back.
        EnableFrameDumps = config.Bind(
            "Debug",
            "Enable Frame Dumps",
            false,
            "Let F1 (or a 'dump.trigger' file next to NOVR.dll) write a buffer dump of the current frame. Off for normal play: a dump stalls the frame and writes several MB of PNGs, which is not what F1 should do to someone who only wanted to fly.");

        RenderDocCaptureOnDump = config.Bind(
            "Debug",
            "RenderDoc Capture On Dump",
            false,
            "When RenderDoc is injected into the game, also trigger a GPU frame capture whenever a buffer dump fires. Has no effect without RenderDoc — see tools/README.md.");

        AutoStartMission = config.Bind(
            "Debug",
            "Auto Start Mission",
            false,
            "Test harness: automatically start a mission from the main menu and fire buffer dumps, so an unattended run produces per-eye dumps and GPU captures with nobody at the keyboard. Leave off for normal play.");

        AutoStartMissionName = config.Bind(
            "Debug",
            "Auto Start Mission Name",
            "",
            "Which mission Auto Start Mission loads, matched case-insensitively against the mission name. Empty picks a Free Flight mission (fastest to load, aircraft already airborne with the HUD up), falling back to the first single-player mission.");

        AutoDumpCount = config.Bind(
            "Debug",
            "Auto Dump Count",
            3,
            new ConfigDescription(
                "How many dumps Auto Start Mission fires once the mission is running. More than one catches frames where the HUD has finished initialising.",
                new AcceptableValueRange<int>(1, 10)));

        AutoDumpDelay = config.Bind(
            "Debug",
            "Auto Dump Delay",
            8f,
            new ConfigDescription(
                "Seconds to wait after the mission starts before the first automatic dump, and between dumps.",
                new AcceptableValueRange<float>(1f, 60f)));
        EscalationOverrides.Bind(config);
        ZoomSpeed = config.Bind(
            "VR Zoom",
            "Zoom Speed",
            2.0f,
            new ConfigDescription(
                "How quickly headset magnification changes while Zoom View is held, in magnification units per second.",
                new AcceptableValueRange<float>(0.1f, 20.0f)));

        MaximumZoom = config.Bind(
            "VR Zoom",
            "Maximum Zoom",
            4.0f,
            new ConfigDescription(
                "Maximum binocular-style headset magnification.",
                new AcceptableValueRange<float>(1.0f, 10.0f)));

        InstantZoomOut = config.Bind(
            "VR Zoom",
            "Instant Zoom Out",
            false,
            "When enabled, any Zoom View out input immediately returns the headset view to 1x magnification.");
    }
}
