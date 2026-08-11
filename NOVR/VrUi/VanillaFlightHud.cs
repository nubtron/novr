using BepInEx.Configuration;

namespace NOVR.VrUi;

/// <summary>
/// Runs the game's flight HUD exactly as it ships, with none of this mod's HUD
/// work applied — an A/B switch, not a feature.
///
/// The mod does not draw its own HUD: it takes the game's <c>FlightHud</c>,
/// converts its canvas to world space, and replaces the placement of every
/// symbol, because the game computes those positions against a single mono
/// camera. That is a large amount of machinery standing between the pilot and
/// the game's own symbology, and there was no way to look at what is underneath
/// it. This setting removes the machinery for one launch so the difference can
/// be seen rather than argued about.
///
/// It works by refusing the patches rather than by making them return early:
/// each flight-HUD patch class gates its <c>[HarmonyPrepare]</c> on this, so
/// when the setting is on the methods are never patched at all and the game's
/// code path is genuinely the original one. A runtime guard would leave our
/// prefixes in the call chain and could not honestly be called vanilla.
///
/// Read once, at patch time. Changing it needs a restart, which is inherent:
/// Harmony decides what to patch during startup.
/// </summary>
[ConfigSection(Order = 95)]
public static class VanillaFlightHud
{
    public static ConfigEntry<bool> Enabled;

    public static void Bind(ConfigFile config)
    {
        Enabled = config.Bind(
            "Experimental",
            "Vanilla Flight HUD",
            false,
            "Diagnostic. Leaves the game's flight HUD completely unmodified: no world-space "
            + "canvas conversion, no VR reprojection of the symbols, no HUD scale, element "
            + "scale, line thickness or pitch-ladder settings, no VR pitch compass. The HUD "
            + "is very likely to be wrong in VR with this on — that is the point of looking "
            + "at it. Menus, cursor, colour grade and everything outside the flight HUD are "
            + "unaffected. Requires a restart.");
    }

    /// <summary>
    /// True when the mod's HUD work should be applied — i.e. normal operation.
    ///
    /// Null-safe on purpose: this is read from <c>[HarmonyPrepare]</c>, and if
    /// the section were ever not bound the honest failure is "behave as we
    /// always have", not "silently ship the diagnostic path".
    /// </summary>
    public static bool ModdedHudActive => Enabled == null || !Enabled.Value;
}
