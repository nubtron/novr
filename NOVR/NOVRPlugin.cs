using System;
using System.IO;
using System.Reflection;
using BepInEx;
using HarmonyLib;
using NOVR.VrCamera;
using NOVR.VrUi;
using NOVR.VrUi.SpecialBehavior;
using UnityEngine.SceneManagement;
using UnityEngine.XR;

#if CPP
using BepInEx.Unity.IL2CPP;
using Il2CppInterop.Runtime.Injection;
#endif

namespace NOVR;

[BepInPlugin(
    "deltawing.novr",
    "NOVR",
    "0.4.3")]
public class NOVRPlugin : BaseUnityPlugin
{
    
    private const string HarmonyId = "deltawing.novr";

    private static NOVRPlugin _instance;
    private static Harmony? _harmony;
    public static string ModFolderPath { get; private set; }

    public NOVRPlugin()
    {
        _instance = this;
        ModFolderPath = Path.GetDirectoryName(Assembly.GetAssembly(typeof(NOVRPlugin)).Location);

        new ModConfiguration(Config);

        if (ShouldDisableVr())
        {
            Logger.LogWarning("NOVR disabled (config `Disable VR Mod` or `--no-vr` launch flag). Running the game unmodified: no patches applied, XR never started.");
            return;
        }

        InputTracking.trackingAcquired += TrackingAcquired;
        _harmony = new Harmony(HarmonyId);
        _harmony.PatchAll(Assembly.GetExecutingAssembly());
        Core.Create();
    }

    /// <summary>
    /// Undo everything the constructor did, leaving the game running vanilla.
    /// Called by <see cref="Core"/> when XR cannot start — without a headset the
    /// mod is not merely idle, it is actively harmful: the VR cursor rebinds
    /// every <c>InputSystemUIInputModule</c> action to a VirtualMouse whose
    /// position comes from a head that does not exist, so menu clicks are
    /// delivered but always land in the same wrong place.
    ///
    /// Safe only because <see cref="Core"/> starts XR before it builds the VR
    /// UI, so on this path the cursor was never constructed and none of that
    /// has happened yet. There is nothing to restore, only work to not do.
    /// </summary>
    internal static void StandDown(string reason)
    {
        _instance?.Logger.LogWarning(
            $"NOVR standing down: {reason} Reverting to vanilla — Harmony patches removed, no VR UI, no cursor takeover. " +
            "Start SteamVR (or your runtime) before the game to fly in VR; pass --no-vr to skip this check entirely.");

        if (_instance != null)
        {
            InputTracking.trackingAcquired -= _instance.TrackingAcquired;
        }

        _harmony?.UnpatchSelf();
        _harmony = null;
    }

    private static bool ShouldDisableVr()
    {
        bool HasFlag(string flag)
        {
            foreach (var arg in Environment.GetCommandLineArgs())
            {
                if (string.Equals(arg, flag, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        // Explicit flags win over the config: --no-vr forces vanilla,
        // --vr forces VR on even when Disable VR Mod is set (handy for a
        // dedicated "NOVR mode" shortcut next to a vanilla default launch).
        if (HasFlag("--no-vr"))
        {
            return true;
        }

        if (HasFlag("--vr"))
        {
            return false;
        }

        return ModConfiguration.Instance.DisableVrMod.Value;
    }

    private void TrackingAcquired(XRNodeState obj)
    {
        NOVRHeadsetData.CalibrateTranslation();
    }
     
    private void Awake()
    {

    }
    
    
}
