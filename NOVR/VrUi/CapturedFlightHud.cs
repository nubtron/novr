using BepInEx.Configuration;

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
    public static ConfigEntry<bool> Enabled;
    public static ConfigEntry<float> Distance;
    public static ConfigEntry<float> FieldOfView;
    public static ConfigEntry<float> Brightness;

    public static void Bind(ConfigFile config)
    {
        Enabled = config.Bind(
            "Experimental",
            "Captured Flight HUD",
            false,
            "Show the game's own flight HUD on a single flat panel fixed in the cockpit, like a "
            + "real HUD combiner, instead of converting the HUD canvas to world space and "
            + "re-projecting each symbol. Nothing is re-projected, so nothing lands in the wrong "
            + "place — but a flat panel cannot conform to the world either: the velocity vector "
            + "and target markers show where the flat HUD drew them, not where they are from the "
            + "headset. Implies Vanilla Flight HUD. Requires a restart.");

        Distance = config.Bind(
            "Experimental",
            "Captured HUD Distance",
            25f,
            new ConfigDescription(
                "Metres in front of the pilot to place the HUD panel. This stands in for "
                + "collimation rather than setting the apparent size (which follows Captured HUD "
                + "Field Of View): far away means near-zero stereo disparity, so the symbology "
                + "reads as projected at infinity the way a real HUD does. Closer than about 10 m "
                + "it starts to read as a screen hanging in the cockpit.",
                new AcceptableValueRange<float>(2f, 200f)));

        FieldOfView = config.Bind(
            "Experimental",
            "Captured HUD Field Of View",
            60f,
            new ConfigDescription(
                "Horizontal angle the HUD panel covers, in degrees. The panel carries the whole "
                + "flat frame, so this is the flat game's field of view: 60 puts the screen edges "
                + "about 30 degrees off the nose. Larger spreads the symbols wider and coarser.",
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
    }
}
