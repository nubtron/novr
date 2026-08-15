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
    public static ConfigEntry<float> PanelShading;
    public static ConfigEntry<bool> GazeDesignator;
    public static ConfigEntry<bool> ViewIcons;

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

        PanelShading = config.Bind(
            "Experimental",
            "Captured HMD Panel Shading",
            0.65f,
            new ConfigDescription(
                "How dark the backing behind the weapon readout, the tactical map and the HMD "
                + "number boxes is — the flat game draws them over a translucent black panel with "
                + "a soft edge, which the pilot needs to read green symbols against bright cloud. "
                + "0 turns it off (symbols only, like a HUD combiner, which cannot darken "
                + "anything); 0.65 is the flat game measured, which passes about a third of what "
                + "is behind it. The number is in the flat game's units and is converted for the "
                + "colour space this panel is composited in, so it means the same thing here as "
                + "it does there. Only the helmet visor is shaded — the airframe HUD panel is "
                + "combiner glass and stays purely additive. Takes effect immediately.",
                new AcceptableValueRange<float>(0f, 1f)));

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

        ViewIcons = config.Bind(
            "Experimental",
            "View Icons",
            true,
            "Put the game's screen-space icons — unit markers, the target designator, the "
            + "selected-target edge arrow, radar warnings, missile notch cues and the objective "
            + "pointer — on a head-locked virtual screen, the way the flat game spreads them "
            + "across the whole screen and the screen follows the view. Icons appear wherever "
            + "you look, the designator sits fixed at the centre of your view (Select picks the "
            + "marker under it), and an off-view selected target becomes the game's own edge "
            + "arrow pinned to the virtual screen's edge. The screen spans Captured HMD Field Of "
            + "View. Only active while Captured Flight HUD is on. Takes effect immediately.");
    }
}
