using BepInEx.Configuration;
using UnityEngine;

namespace NOVR.VrUi;

/// <summary>
/// Settings for the captured flight HUD — the game's own HUD shown on one flat
/// panel fixed in the cockpit instead of taken apart and re-projected symbol by
/// symbol. See <c>VrUi/Capture/FlightHudCaptureBackend.cs</c> for what it does.
///
/// Turning this on implies <see cref="VanillaFlightHud"/>: the HUD has to stay
/// in ScreenSpaceOverlay for there to be an overlay pass to capture, so the same
/// eight patches are withheld. That is why the two are wired together rather
/// than left as two switches a user has to get right — "captured HUD on, vanilla
/// HUD off" has no sensible meaning and would silently produce a blank panel.
///
/// Read at patch time, so it needs a restart.
/// </summary>
[ConfigSection(Order = 96)]
public static class CapturedFlightHud
{
    /// <summary>
    /// Metres. Far enough that the panel is collimated in every cue the eye
    /// has: 0.43 arc-minutes of stereo disparity across a 63 mm IPD (a fifth
    /// of a pixel on a 25-PPD headset, against a stereo threshold of about
    /// one arc-minute) and 0.7 arc-minutes of parallax per 10 cm of head
    /// movement. See <see cref="Distance"/>'s description for why the panel
    /// costs nothing to move out.
    /// </summary>
    public const float DefaultDistanceMeters = 500f;

    private const float MinDistanceMeters = 2f;
    private const float MaxDistanceMeters = 1000f;

    public static ConfigEntry<bool> Enabled;
    public static ConfigEntry<bool> Conformal;
    public static ConfigEntry<float> Distance;
    public static ConfigEntry<float> FieldOfView;
    public static ConfigEntry<float> Brightness;
    public static ConfigEntry<bool> PixelSnap;
    public static ConfigEntry<bool> SmoothDownscale;

    /// <summary>
    /// The panel distance the backends place at, clamped and defaulted in one
    /// place. Four call sites shared the literals before, which is how the
    /// value and the arithmetic that justified it drifted apart unnoticed.
    /// </summary>
    public static float DistanceMeters =>
        Mathf.Clamp(Distance?.Value ?? DefaultDistanceMeters, MinDistanceMeters, MaxDistanceMeters);

    /// <summary>
    /// Snap the captured UI to the capture texture's pixel grid. Null-safe like
    /// the rest: an unbound section must not silently change how the HUD draws.
    /// </summary>
    public static bool PixelSnapEnabled => PixelSnap != null && PixelSnap.Value;

    /// <summary>Filter the capture down with mipmaps instead of two taps.</summary>
    public static bool SmoothDownscaleEnabled => SmoothDownscale != null && SmoothDownscale.Value;


    public static void Bind(ConfigFile config)
    {
        Enabled = config.Bind(
            "Experimental",
            "Captured Flight HUD",
            true,
            "Show the game's own flight HUD on a single flat panel fixed in the cockpit, like a "
            + "real HUD combiner, instead of converting the HUD canvas to world space and "
            + "re-projecting each symbol. Nothing is re-projected, so nothing lands in the wrong "
            + "place — but a flat panel cannot conform to the world either: the velocity vector "
            + "and target markers show where the flat HUD drew them, not where they are from the "
            + "headset. Implies Vanilla Flight HUD. Requires a restart.");

        Conformal = config.Bind(
            "Experimental",
            "Captured HUD Conformal",
            true,
            "Make the world line up with the symbols the way a real HUD does: the game's HUD code "
            + "is made to project from a fixed 'design eye' at the cockpit camera mount, "
            + "boresighted with the airframe, at exactly the panel's field of view — instead of "
            + "from your head. The airframe-fixed panel then shows every symbol on its true ray: "
            + "the velocity vector sits on where you are going, unit markers sit on their units, "
            + "and the pitch ladder stays horizon-parallel as the aircraft rolls. Head movement "
            + "is absorbed by the panel distance, like a real collimator — recenter facing the "
            + "boresight, since the whole frame is only as aligned as your recenter. (The ladder's "
            + "line spacing stays as the flat game draws it — expanded for readability — so its "
            + "lines are not true angles; that is the game's choice, reproduced faithfully.) "
            + "Off = the same fixed panel, but symbols drawn from the head camera, which do not "
            + "line up with the world. Takes effect immediately.");

        Distance = config.Bind(
            "Experimental",
            "Captured HUD Distance",
            DefaultDistanceMeters,
            new ConfigDescription(
                "Metres in front of the pilot to place the HUD panel. This stands in for "
                + "collimation rather than setting the apparent size (which follows Captured HUD "
                + "Field Of View): a real combiner projects at infinity, so its symbology has no "
                + "stereo disparity and does not slide against the world when you move your head, "
                + "and a distant panel is how that is approximated. Every error scales as one "
                + "over the distance, so the panel is cheap to push out — the width is derived "
                + "from the angle, which holds the apparent size and the texture's angular "
                + "resolution constant however far away it is. At the default 500 m the disparity "
                + "is 0.43 arc-minutes and a 10 cm lean moves the symbology 0.7 arc-minutes "
                + "against the world; both are under what the eye resolves. Bring it in and it "
                + "starts to read as a screen hanging in the cockpit instead: 25 m is 8.8 "
                + "arc-minutes and 14 arc-minutes per 10 cm, which is several pixels of split "
                + "between the eyes and visible sliding as you move.",
                new AcceptableValueRange<float>(MinDistanceMeters, MaxDistanceMeters)));

        FieldOfView = config.Bind(
            "Experimental",
            "Captured HUD Field Of View",
            60f,
            new ConfigDescription(
                "Horizontal angle the HUD panel covers, in degrees. In conformal mode this is "
                + "also the design eye's field of view, so it stays the angle the symbols are "
                + "true at: 60 puts the screen edges about 30 degrees off the nose. Larger "
                + "spreads the frame wider (and targets stay on-panel further off boresight) but "
                + "draws everything coarser.",
                new AcceptableValueRange<float>(20f, 120f)));

        Brightness = config.Bind(
            "Experimental",
            "Captured HUD Brightness",
            2f,
            new ConfigDescription(
                "How hard the HUD panel is driven. The panel adds light rather than covering the "
                + "view, like a real combiner, and the captured texture arrives pre-multiplied by "
                + "the HUD's own alpha — so at 1.0 it washes out against daylit cloud. Raise it "
                + "until the symbology reads against the brightest sky you fly in; lower it if it "
                + "glares at night. Takes effect immediately.",
                new AcceptableValueRange<float>(0.25f, 8f)));

        PixelSnap = config.Bind(
            "Experimental",
            "Captured HUD Pixel Snap",
            false,
            "Round the captured HUD and helmet-visor geometry to whole pixels of the texture it "
            + "is drawn into, the way a flat game keeps its UI on the pixel grid so a thin stroke "
            + "does not straddle two pixels. Off by default because it was measured to change "
            + "the captured image by nothing worth seeing: the share of stroke pixels reaching "
            + "full brightness moved from 14.2 to 14.7 per cent at a 2560x1440 window and from "
            + "8.8 to 8.6 at 1920x1080, which is run-to-run noise. The game's HUD is laid out in "
            + "a 1920x1080 reference space and scaled to the window, and much of it is rotated "
            + "or sized in fractions of a pixel, so there is little for a grid to catch. It is "
            + "here to be tried in a headset, which is the only place the difference could show. "
            + "Takes effect immediately.");

        SmoothDownscale = config.Bind(
            "Experimental",
            "Captured HUD Smooth Downscale",
            false,
            "Give the HUD panel's capture mipmaps, trilinear filtering and 8x anisotropy, so the "
            + "GPU averages the texels it skips instead of taking two samples and hoping. The "
            + "panel is shown smaller than it is captured — a 2560-wide window across a 60 "
            + "degree panel is 43 texels per degree, against roughly 27 eye pixels per degree on "
            + "a Quest 3 — and two samples cannot carry that, so thin strokes flicker and drop "
            + "out as the aircraft moves. This is the fix for that, and it costs a little "
            + "softness in a still frame, which is why it is off by default: if the HUD looks "
            + "unsettled in motion, turn it on; if it looks soft standing still, leave it off. "
            + "The helmet visor is left alone either way — 1920 pixels across 70 degrees is "
            + "about one texel per eye pixel already, where a mipmap would only blur it. Takes "
            + "effect immediately.");
    }
}
