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
    
    private static NOVRPlugin _instance;
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
        Harmony.CreateAndPatchAll(Assembly.GetExecutingAssembly());
        Core.Create();
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
