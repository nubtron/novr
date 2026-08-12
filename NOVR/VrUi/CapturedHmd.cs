using BepInEx.Configuration;

namespace NOVR.VrUi;

/// <summary>
/// Settings for the captured helmet-mounted display and the gaze target
/// designator — the base game's own head-tracking systems, carried into VR
/// with their code unmodified. See <c>VrUi/Capture/HmdVisorBackend.cs</c> and
/// <c>VrUi/Components/GazeDesignatorDriver.cs</c> for what they do.
///
/// Both only act while the captured flight HUD is active
/// (<see cref="CapturedFlightHud"/>): they are the head-referenced half of the
/// same faithful-to-the-original arrangement, and without the captured HUD the
/// modded world-space path has its own equivalents.
/// </summary>
[ConfigSection(Order = 95)]
public static class CapturedHmd
{
    public static ConfigEntry<bool> Visor;
    public static ConfigEntry<float> VisorFieldOfView;
    public static ConfigEntry<bool> GazeDesignator;

    public static void Bind(ConfigFile config)
    {
        Visor = config.Bind(
            "Experimental",
            "Captured HMD Visor",
            true,
            "Show the game's own helmet-mounted display — the minimal speed/altitude/bearing/"
            + "horizon readouts the flat game glues to the view — on a head-locked panel, the way "
            + "the original game glues them to the screen. The game's own declutter runs "
            + "unmodified: readouts near the nose hide when you look at the HUD and return when "
            + "you look away (the HMD Hide Distance game setting works again). Only active while "
            + "Captured Flight HUD is on. Takes effect immediately.");

        VisorFieldOfView = config.Bind(
            "Experimental",
            "Captured HMD Field Of View",
            70f,
            new ConfigDescription(
                "Horizontal angle the helmet visor panel covers, in degrees. The flat game "
                + "spreads the HMD readouts across the whole screen; this is how much of your "
                + "view that screen maps to.",
                new AcceptableValueRange<float>(30f, 110f)));

        GazeDesignator = config.Bind(
            "Experimental",
            "Gaze Target Designator",
            true,
            "Restore the original game's look-to-select: the target designator follows your "
            + "gaze across the HUD, and Select picks the marker under it — the game's own "
            + "selection code (radius, priority, painting, the edge arrow) runs unmodified, it "
            + "is only handed where you are actually looking instead of a fixed screen centre. "
            + "Only active while Captured Flight HUD is on with Conformal. Takes effect "
            + "immediately.");
    }
}
