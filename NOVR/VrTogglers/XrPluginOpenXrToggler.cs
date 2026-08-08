using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.XR.Management;
using UnityEngine.XR.OpenXR;
using UnityEngine.XR.OpenXR.Features;
using UnityEngine.XR.OpenXR.Features.Interactions;

namespace NOVR.VrTogglers;

public class XrPluginOpenXrToggler : XrPluginToggler
{
    // `features` is internal to the Unity.XR.OpenXR assembly, so it is set
    // via reflection from here.
    private static readonly FieldInfo FeaturesField = typeof(OpenXRSettings).GetField(
        "features", BindingFlags.Instance | BindingFlags.NonPublic);

    protected override XRLoader CreateLoader()
    {
        var xrLoader = ScriptableObject.CreateInstance<OpenXRLoader>();
        return xrLoader;
    }

    protected override bool SetUp()
    {
        EnableControllerInteractionProfiles();
        return base.SetUp();
    }

    /// <summary>
    /// OpenXR only exposes motion controllers when at least one interaction
    /// profile is registered — otherwise the session starts with "No action
    /// sets" and no controller devices ever appear. The NOVR OpenXR settings
    /// ship with an empty feature list, so register the common controller
    /// profiles here, before the OpenXR instance is created.
    /// </summary>
    private static void EnableControllerInteractionProfiles()
    {
        var settings = OpenXRSettings.Instance;
        if (settings == null || FeaturesField == null)
        {
            return;
        }

        var existing = FeaturesField.GetValue(settings) as OpenXRFeature[];
        var features = new List<OpenXRFeature>();
        if (existing != null)
        {
            features.AddRange(existing);
        }

        AddProfileIfMissing<OculusTouchControllerProfile>(features);
        AddProfileIfMissing<MetaQuestTouchPlusControllerProfile>(features);
        AddProfileIfMissing<MetaQuestTouchProControllerProfile>(features);
        AddProfileIfMissing<KHRSimpleControllerProfile>(features);

        FeaturesField.SetValue(settings, features.ToArray());
        Debug.Log($"[NOVR] Enabled OpenXR controller interaction profiles ({features.Count} features registered)");
    }

    private static void AddProfileIfMissing<T>(ICollection<OpenXRFeature> features) where T : OpenXRFeature
    {
        foreach (var feature in features)
        {
            if (feature != null && feature.GetType() == typeof(T))
            {
                feature.enabled = true;
                return;
            }
        }

        var instance = ScriptableObject.CreateInstance<T>();
        instance.enabled = true;
        features.Add(instance);
    }
}
