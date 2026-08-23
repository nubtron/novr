using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace NOVR.VrCamera;

/// <summary>
/// Recentre the VR view by holding the map button down.
///
/// <para><b>Why the map button.</b> Recentring is the one thing a pilot needs
/// mid-flight that no controller they are already holding has a button for.
/// The mod's other route is a keyboard key, which in a headset means finding a
/// key you cannot see, and a HOTAS pilot has no spare button to bind — every
/// one is already an aircraft function. The map button is bound on every
/// setup, is reachable without letting go of anything, and holding it is a
/// gesture the flat game does not use.</para>
///
/// <para><b>Why the map does not open.</b> A hold that also toggled the map
/// would leave the pilot recentred and staring at a map they did not ask for.
/// So the map moves to the release of a <i>short</i> press and the recentre
/// takes the long one — which is the game's own idiom, not an invention:
/// <c>ExtraUiInput</c> already reads "Cancel" as
/// <c>GetButtonTimedPressUp(…, 0, clickDelay)</c> for deselect-one and
/// <c>GetButtonTimedPressDown(…, pressDelay)</c> for deselect-all. A tap opens
/// the map on release instead of on press, which is a frame or two later and
/// not perceptible.</para>
///
/// <para><b>Why a transpiler.</b> <c>ExtraUiInput.Update</c> does two unrelated
/// jobs — the map toggle and the target-deselect — so a prefix cannot take one
/// and leave the other. The one call that decides the map, and nothing else in
/// the process, is rewritten to <see cref="MapPressed"/>.</para>
/// </summary>
internal static class MapHoldRecenter
{
    private const string MapAction = "Map";

    /// <summary>
    /// Stands in for <c>Player.GetButtonDown("Map")</c>: true when the map
    /// should toggle, and never true on the frame a hold recentres.
    ///
    /// <para>Takes the same two stack arguments the instance call it replaces
    /// did, which is what lets the rewrite be one instruction.</para>
    /// </summary>
    public static bool MapPressed(Rewired.Player player, string action)
    {
        if (player == null) return false;
        if (!MapHoldRecenterConfig.Enabled) return player.GetButtonDown(action);

        var hold = MapHoldRecenterConfig.HoldSeconds;

        // Fires once, on the frame the hold crosses the threshold, with the
        // button still down — Rewired suppresses it on every later frame by
        // checking that the previous frame was still short of it.
        if (player.GetButtonTimedPressDown(action, hold))
        {
            Recentre();
            return false;
        }

        // Anything shorter still toggles the map, on release rather than on
        // press. Note this is not "not a long press": a hold released after
        // the recentre has already fired matches neither, so letting go does
        // not open the map on the way out.
        return player.GetButtonTimedPressUp(action, 0f, hold);
    }

    private static void Recentre()
    {
        // The map button is a keyboard key on most setups, and a mission name
        // being typed into a text field is not a request to recentre.
        if (global::NuclearOption.MissionEditorScripts.InputFieldChecker.InsideInputField) return;

        NOVRHeadsetData.CalibrateTranslation();
        NOVRHeadsetData.CalibrateRotation();
        Debug.Log($"[NOVR] View recentred: the map button was held for " +
                  $"{MapHoldRecenterConfig.HoldSeconds:0.0}s.");
    }

    private static readonly MethodInfo GetButtonDownMethod =
        AccessTools.Method(typeof(Rewired.Player), nameof(Rewired.Player.GetButtonDown), new[] { typeof(string) });
    private static readonly MethodInfo MapPressedMethod =
        AccessTools.Method(typeof(MapHoldRecenter), nameof(MapPressed));

    [HarmonyPatch(typeof(global::ExtraUiInput), "Update")]
    private static class ExtraUiInputUpdatePatch
    {
        [HarmonyTranspiler]
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var code = new List<CodeInstruction>(instructions);
            var replaced = 0;

            for (var i = 1; i < code.Count; i++)
            {
                // Matched as the pair, not as the call alone: this method is
                // free to grow another GetButtonDown for something that is not
                // the map, and rewriting that one would move a control the
                // pilot never asked us to touch.
                if (!code[i].Calls(GetButtonDownMethod)) continue;
                if (code[i - 1].opcode != OpCodes.Ldstr) continue;
                if ((string)code[i - 1].operand != MapAction) continue;

                code[i] = new CodeInstruction(OpCodes.Call, MapPressedMethod)
                {
                    labels = code[i].labels,
                    blocks = code[i].blocks,
                };
                replaced++;
            }

            // A transpiler that matches nothing applies cleanly and does
            // nothing, and there is no other symptom: the map would keep
            // working and the hold would simply never recentre.
            if (replaced != 1)
            {
                Debug.LogError($"[NOVR] {nameof(MapHoldRecenter)} rewrote {replaced} " +
                               "GetButtonDown(\"Map\") calls in ExtraUiInput.Update, expected 1. " +
                               "Holding the map button will not recentre the view.");
            }

            return code;
        }
    }
}

/// <summary>
/// The settings live here rather than in ModConfiguration because this mod is
/// developed as a stack of independent branches, and a setting declared in the
/// shared file puts every layer that adds one in conflict with every other.
/// See <see cref="ConfigSectionAttribute"/>.
/// </summary>
[ConfigSection(Order = 55)]
public static class MapHoldRecenterConfig
{
    private const string Section = "General";

    public static ConfigEntry<bool> MapHoldRecenterEnabled;
    public static ConfigEntry<float> MapHoldRecenterSeconds;

    // Every read goes through these: ConfigSections catches a section that
    // fails to bind and carries on, so an entry can legitimately be null and
    // the map button must keep working when it is.
    public static bool Enabled => MapHoldRecenterEnabled != null && MapHoldRecenterEnabled.Value;
    public static float HoldSeconds =>
        MapHoldRecenterSeconds != null ? Mathf.Max(MapHoldRecenterSeconds.Value, 0.25f) : 3f;

    public static void Bind(ConfigFile config)
    {
        MapHoldRecenterEnabled = config.Bind(
            Section,
            "Map Hold Recenter",
            true,
            "Hold the map button down for a few seconds to recenter the VR view, so a headset that has drifted can be straightened without reaching for the keyboard. A normal tap still opens and closes the map — it just does it when you let go rather than when you press, which is a frame or two later and is what leaves room for the hold. A hold that recenters does not open the map. Off restores the plain press, and the recenter shortcut key is unaffected either way.");

        MapHoldRecenterSeconds = config.Bind(
            Section,
            "Map Hold Recenter Seconds",
            3f,
            new ConfigDescription(
                "How long the map button must be held before the view recenters. Long enough that no ordinary use of the map reaches it, short enough to hold while flying: three seconds. Below about one second a slow tap starts recentering by accident, and every value here also delays nothing else — a shorter hold does not make the map open any sooner.",
                new AcceptableValueRange<float>(0.25f, 10f)));
    }
}
