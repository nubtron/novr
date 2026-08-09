using System;
using System.Globalization;
using BepInEx.Configuration;
using HarmonyLib;
using NuclearOption.Networking;
using NuclearOption.SavedMission;

namespace NOVR;

/// <summary>
/// Optional overrides for the Escalation map-mode economy (and any other mission).
///
/// Everything is OFF by default: an override value of -1 (floats/ints) or an empty
/// string means "use the mission's own value", so the mod behaves exactly like
/// stock unless the user opts in.
///
/// Why these exist: the important economy parameters (kill reward, tax rate,
/// regular income, escalation thresholds, sortie bonus, warhead stockpile, AI
/// aircraft cap, …) are all set per-mission and are tunable in the game's
/// in-game mission editor — but only for *custom* missions. The stock built-in
/// "Escalation" mission (and every other built-in) is embedded read-only in the
/// game data and cannot be edited in place. These overrides let you retune the
/// stock mission — or apply a global economy across every mission — from
/// `deltawing.novr.cfg` without touching the mission editor.
///
/// Mechanism:
///  * A Harmony PREFIX on MissionManager.SetMission (host/server only — clients
///    receive the mission over the network, not via SetMission) rewrites the
///    fields on the freshly-deserialized Mission object before anything consumes
///    it: FactionHQ.OnMissionLoad copies the faction values, MissionManager
///    .StartMission copies the escalation thresholds, Aircraft reads
///    successfulSortieBonus live, etc. Since the server is authoritative and
///    broadcasts the resulting syncvars, clients see the overridden values.
///  * Rank Thresholds Override and Support Reward Multiplier patch values the
///    game hardcodes in the assembly (the rank curve array and the non-kill
///    reward formulas respectively), which the mission editor cannot touch.
/// </summary>
public static class EscalationOverrides
{
    private const string Section = "Escalation";

    // ---- mission-level (MissionSettings) overrides; -1 = use mission value ----
    public static ConfigEntry<float> TacticalThresholdOverride;      // nuclearEscalationThreshold   (Escalation stock 625)
    public static ConfigEntry<float> StrategicThresholdOverride;     // strategicEscalationThreshold (Escalation stock 1225)
    public static ConfigEntry<float> SortieBonusOverride;            // successfulSortieBonus        (Escalation stock 0.75)
    public static ConfigEntry<float> RankMultiplierOverride;         // rankMultiplier               (Escalation stock 0.75)
    public static ConfigEntry<int> MinRankTacticalWarheadOverride;   // minRankTacticalWarhead       (stock 0)
    public static ConfigEntry<int> MinRankStrategicWarheadOverride;  // minRankStrategicWarhead      (stock 0)

    // ---- per-faction (MissionFaction) overrides; -1 = use mission value ----
    public static ConfigEntry<float> StartingBalanceOverride;            // stock 1002.7
    public static ConfigEntry<float> PlayerJoinAllowanceOverride;        // stock 45
    public static ConfigEntry<float> TaxRateOverride;                    // stock 0.25
    public static ConfigEntry<float> RegularIncomeOverride;              // stock 2.06
    public static ConfigEntry<float> ExcessFundsDistributeOverride;      // stock 0.25
    public static ConfigEntry<float> KillRewardOverride;                 // stock 3.0
    public static ConfigEntry<int> StartingWarheadsOverride;             // stock 16
    public static ConfigEntry<int> ReserveWarheadsOverride;              // stock 16
    public static ConfigEntry<int> ReserveAirframesOverride;             // stock 1
    public static ConfigEntry<int> ExtraReservesPerPlayerOverride;       // stock 1
    public static ConfigEntry<int> AIAircraftLimitOverride;              // stock 4
    public static ConfigEntry<float> ReduceAIPerFriendlyPlayerOverride;  // stock 1
    public static ConfigEntry<float> AddAIPerEnemyPlayerOverride;        // stock 1

    // ---- hardcoded-in-assembly overrides (no mission equivalent) ----
    public static ConfigEntry<string> RankThresholdsOverride;        // "0,5,15,30,60,120"; empty = game default
    public static ConfigEntry<float> SupportRewardMultiplier;        // 1.0 = unchanged

    public static void Bind(ConfigFile config)
    {
        // mission-level
        TacticalThresholdOverride = config.Bind(Section, "Tactical Escalation Threshold Override", -1f,
            "Escalation score at which tactical (1.5 kt) nuclear weapons unlock. -1 = use the mission's value (Escalation: 625).");
        StrategicThresholdOverride = config.Bind(Section, "Strategic Escalation Threshold Override", -1f,
            "Escalation score at which strategic (250 kt) nuclear weapons unlock. -1 = use the mission's value (Escalation: 1225).");
        SortieBonusOverride = config.Bind(Section, "Sortie Bonus Override", -1f,
            "Fraction of sortie score banked to both the player and the faction when an aircraft is returned (landed/rearmed). -1 = use the mission's value (Escalation: 0.75).");
        RankMultiplierOverride = config.Bind(Section, "Rank Multiplier Override", -1f,
            "Multiplier easing the rank curve (applied to player score before comparing to rank thresholds). -1 = use the mission's value (Escalation: 0.75).");
        MinRankTacticalWarheadOverride = config.Bind(Section, "Min Rank Tactical Warhead Override", -1,
            "Minimum player rank required to mount tactical nukes, in addition to the escalation gate. -1 = use the mission's value (Escalation: 0).");
        MinRankStrategicWarheadOverride = config.Bind(Section, "Min Rank Strategic Warhead Override", -1,
            "Minimum player rank required to mount strategic nukes, in addition to the escalation gate. -1 = use the mission's value (Escalation: 0).");

        // per-faction
        StartingBalanceOverride = config.Bind(Section, "Starting Balance Override", -1f,
            "Faction treasury at mission start (also sets the excess-funds threshold). -1 = use the mission's value (Escalation: 1002.7).");
        PlayerJoinAllowanceOverride = config.Bind(Section, "Player Join Allowance Override", -1f,
            "Starting allocation paid from faction funds to each player who joins. -1 = use the mission's value (Escalation: 45).");
        TaxRateOverride = config.Bind(Section, "Tax Rate Override", -1f,
            "Share of player kill/capture earnings siphoned to faction funds (player keeps 1 - tax). -1 = use the mission's value (Escalation: 0.25).");
        RegularIncomeOverride = config.Bind(Section, "Regular Income Override", -1f,
            "Base per-player income drip paid out each distribution cycle. -1 = use the mission's value (Escalation: 2.06).");
        ExcessFundsDistributeOverride = config.Bind(Section, "Excess Funds Distribute Override", -1f,
            "Share of funds above the starting balance redistributed to players each cycle. -1 = use the mission's value (Escalation: 0.25).");
        KillRewardOverride = config.Bind(Section, "Kill Reward Override", -1f,
            "Multiplier on sqrt(target value) for kill rewards and faction fund gains. -1 = use the mission's value (Escalation: 3.0).");
        StartingWarheadsOverride = config.Bind(Section, "Starting Warheads Override", -1,
            "Warhead stockpile added to airbases shortly after mission start. -1 = use the mission's value (Escalation: 16).");
        ReserveWarheadsOverride = config.Bind(Section, "Reserve Warheads Override", -1,
            "Warheads held in reserve, unavailable to AI strike units. -1 = use the mission's value (Escalation: 16).");
        ReserveAirframesOverride = config.Bind(Section, "Reserve Airframes Override", -1,
            "Airframes per type never auto-deployed by AI (kept for players). -1 = use the mission's value (Escalation: 1).");
        ExtraReservesPerPlayerOverride = config.Bind(Section, "Extra Reserves Per Player Override", -1,
            "Additional reserve airframes per friendly player. -1 = use the mission's value (Escalation: 1).");
        AIAircraftLimitOverride = config.Bind(Section, "AI Aircraft Limit Override", -1,
            "Base cap on simultaneously active AI aircraft. -1 = use the mission's value (Escalation: 4).");
        ReduceAIPerFriendlyPlayerOverride = config.Bind(Section, "AI Reduction Per Friendly Player Override", -1f,
            "AI aircraft cap reduction per friendly human player. -1 = use the mission's value (Escalation: 1).");
        AddAIPerEnemyPlayerOverride = config.Bind(Section, "AI Addition Per Enemy Player Override", -1f,
            "AI aircraft cap increase per enemy human player. -1 = use the mission's value (Escalation: 1).");

        // hardcoded
        RankThresholdsOverride = config.Bind(Section, "Rank Thresholds Override", "",
            "Comma-separated player-score thresholds for ranks 0-5 (game default: 0,5,15,30,60,120, before the rank multiplier). "
            + "Empty = game default. E.g. '0,10,25,50,100,200' for a slower rank curve.");
        SupportRewardMultiplier = config.Bind(Section, "Support Reward Multiplier", 1.0f,
            "Scales all non-kill support rewards (recon, jamming, supply/rearm, refuel, repair, pilot rescue/capture, airbase capture) "
            + "for players. 1.0 = unchanged; 2.0 doubles support income; 0 disables it. Kill rewards are not affected (tune those with Kill Reward Override).");
    }

    /// <summary>Applies every configured override onto a freshly loaded mission (host/server only).</summary>
    public static void ApplyToMission(Mission mission)
    {
        if (mission == null || ReferenceEquals(mission, Mission.NullMission)) return;
        MissionSettings settings = mission.missionSettings;
        if (settings == null) return;

        settings.nuclearEscalationThreshold = OverrideFloat(TacticalThresholdOverride, settings.nuclearEscalationThreshold);
        settings.strategicEscalationThreshold = OverrideFloat(StrategicThresholdOverride, settings.strategicEscalationThreshold);
        settings.successfulSortieBonus = OverrideFloat(SortieBonusOverride, settings.successfulSortieBonus);
        settings.rankMultiplier = OverrideFloat(RankMultiplierOverride, settings.rankMultiplier);
        settings.minRankTacticalWarhead = OverrideInt(MinRankTacticalWarheadOverride, settings.minRankTacticalWarhead);
        settings.minRankStrategicWarhead = OverrideInt(MinRankStrategicWarheadOverride, settings.minRankStrategicWarhead);

        foreach (MissionFaction faction in mission.factions)
        {
            if (faction == null) continue;
            faction.startingBalance = OverrideFloat(StartingBalanceOverride, faction.startingBalance);
            faction.playerJoinAllowance = OverrideFloat(PlayerJoinAllowanceOverride, faction.playerJoinAllowance);
            faction.playerTaxRate = OverrideFloat(TaxRateOverride, faction.playerTaxRate);
            faction.regularIncome = OverrideFloat(RegularIncomeOverride, faction.regularIncome);
            faction.excessFundsDistributePercent = OverrideFloat(ExcessFundsDistributeOverride, faction.excessFundsDistributePercent);
            faction.killReward = OverrideFloat(KillRewardOverride, faction.killReward);
            faction.startingWarheads = OverrideInt(StartingWarheadsOverride, faction.startingWarheads);
            faction.reserveWarheads = OverrideInt(ReserveWarheadsOverride, faction.reserveWarheads);
            faction.reserveAirframes = OverrideInt(ReserveAirframesOverride, faction.reserveAirframes);
            faction.extraReservesPerPlayer = OverrideInt(ExtraReservesPerPlayerOverride, faction.extraReservesPerPlayer);
            faction.AIAircraftLimit = OverrideInt(AIAircraftLimitOverride, faction.AIAircraftLimit);
            faction.reduceAIPerFriendlyPlayer = OverrideFloat(ReduceAIPerFriendlyPlayerOverride, faction.reduceAIPerFriendlyPlayer);
            faction.addAIPerEnemyPlayer = OverrideFloat(AddAIPerEnemyPlayerOverride, faction.addAIPerEnemyPlayer);
        }
    }

    /// <summary>Applies the rank-threshold override to a player instance (server + client, so display matches).</summary>
    public static void ApplyRankThresholds(NuclearOption.Networking.Player player)
    {
        if (player == null || string.IsNullOrWhiteSpace(RankThresholdsOverride.Value)) return;
        try
        {
            string[] parts = RankThresholdsOverride.Value.Split(',');
            if (parts.Length == 0) return;
            var thresholds = new float[parts.Length];
            for (int i = 0; i < parts.Length; i++)
            {
                if (!float.TryParse(parts[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float t) || t < 0f)
                    return; // invalid entry → leave the game default untouched
                thresholds[i] = t;
            }
            // rankThresholds is a private field; the game reads it in CalculateRank/SetRank/ScoreNeededForNextRank.
            var field = typeof(NuclearOption.Networking.Player).GetField("rankThresholds",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            field?.SetValue(player, thresholds);
        }
        catch (Exception e)
        {
            UnityEngine.Debug.LogWarning($"[NOVR] Rank Thresholds Override failed: {e.Message}");
        }
    }

    private static float OverrideFloat(ConfigEntry<float> entry, float current) => entry.Value < 0f ? current : entry.Value;
    private static int OverrideInt(ConfigEntry<int> entry, int current) => entry.Value < 0 ? current : entry.Value;

    // ------------------------------------------------------------------
    // Harmony patches (auto-registered by NOVRPlugin's CreateAndPatchAll)
    // ------------------------------------------------------------------

    /// <summary>Host/server hook: rewrite the mission before it is consumed.</summary>
    [HarmonyPatch(typeof(MissionManager), "SetMission")]
    private static class SetMissionPatch
    {
        [HarmonyPrefix]
        private static void Prefix(Mission mission)
        {
            ApplyToMission(mission);
        }
    }

    /// <summary>Rank thresholds are hardcoded per Player instance; overwrite them on spawn.</summary>
    [HarmonyPatch(typeof(NuclearOption.Networking.Player), "OnStartServer")]
    private static class RankThresholdsServerPatch
    {
        [HarmonyPostfix]
        private static void Postfix(NuclearOption.Networking.Player __instance)
        {
            ApplyRankThresholds(__instance);
        }
    }

    [HarmonyPatch(typeof(NuclearOption.Networking.Player), "OnStartClient")]
    private static class RankThresholdsClientPatch
    {
        [HarmonyPostfix]
        private static void Postfix(NuclearOption.Networking.Player __instance)
        {
            ApplyRankThresholds(__instance);
        }
    }

    /// <summary>Uniform multiplier for all non-kill support rewards (recon/jam/supply/refuel/repair/pilots/capture).</summary>
    [HarmonyPatch(typeof(FactionHQ), "RewardPlayer")]
    private static class RewardPlayerPatch
    {
        [HarmonyPrefix]
        private static void Prefix(FactionHQ.RewardType missionType, ref float rewardAllocation, ref float rewardScore)
        {
            if (missionType == FactionHQ.RewardType.Kill || missionType == FactionHQ.RewardType.None) return;
            float m = SupportRewardMultiplier.Value;
            if (Math.Abs(m - 1.0f) < 0.0001f) return;
            rewardAllocation *= m;
            rewardScore *= m;
        }
    }
}
