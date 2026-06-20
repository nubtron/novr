using System;
using System.Reflection;
using System.Text.RegularExpressions;
using NOVR.VrCamera;
using NOVR.VrTogglers;
using NOVR.VrUi;
using UnityEngine;
using UnityEngine.InputSystem.XR;
using UnityEngine.SceneManagement;
using UnityEngine.XR;

namespace NOVR;

public class Core : MonoBehaviour
{
    
    private float _originalFixedDeltaTime;

    private NOVRHeadsetData? _headsetData;
    private NOUIManager? _vrUi;
    private PropertyInfo? _refreshRateProperty;
    private VrTogglerManager? _vrTogglerManager;
    
    private Aircraft _aircraft;
    private Aircraft _oldAircraft;

    public static string CurrentAircraftId { get; private set; }

    public static void Create()
    {
        new GameObject("NOVR").AddComponent<Core>();
    }

    private void Awake()
    {
        DontDestroyOnLoad(gameObject);
        gameObject.AddComponent<VrCameraManager>();
        gameObject.AddComponent<DebugDumpController>();
        gameObject.AddComponent<AutoStartMission>();
        gameObject.AddComponent<APIBus>();
        gameObject.AddComponent<ColorGradeController>();
        gameObject.AddComponent<MotionControllerVisual>();
    }

    private void OnDestroy()
    {
        Debug.Log("NOVR has been destroyed. This shouldn't have happened. Recreating...");
        
        Create();
    }

    private void Start()
    {
        
        
        var xrDeviceType = Type.GetType("UnityEngine.XR.XRDevice, UnityEngine.XRModule") ??
                           Type.GetType("UnityEngine.XR.XRDevice, UnityEngine.VRModule") ??
                           Type.GetType("UnityEngine.VR.VRDevice, UnityEngine.VRModule") ??
                           Type.GetType("UnityEngine.VR.VRDevice, UnityEngine");

        _refreshRateProperty = xrDeviceType?.GetProperty("refreshRate");
        
        _headsetData = NOVRBehaviour.Create<NOVRHeadsetData>(transform);
        _vrUi = NOVRBehaviour.Create<NOUIManager>(transform);
        
        _vrTogglerManager = new VrTogglerManager();
        
    }



    private void Update()
    {
        UpdatePhysicsRate();
    }

    private void UpdatePhysicsRate()
    {
        if (_originalFixedDeltaTime == 0)
        {
            _originalFixedDeltaTime = Time.fixedDeltaTime;
        }

        if (_refreshRateProperty == null) return;

        var headsetRefreshRate = (float)_refreshRateProperty.GetValue(null, null);
        if (headsetRefreshRate <= 0) return;


        Time.fixedDeltaTime = _originalFixedDeltaTime;
        
    }
    private void FixedUpdate()
    {
        _oldAircraft = _aircraft;
        GameManager.GetLocalAircraft(out _aircraft);
        if (_aircraft != _oldAircraft)
        {
            CurrentAircraftId = ResolveAircraftId(_aircraft);
            if (ModConfiguration.Instance.TryGetSavedOffset(CurrentAircraftId, out var f, out var r))
            {
                ModConfiguration.Instance.CockpitHeadForwardOffset.Value = f;
                ModConfiguration.Instance.CockpitHeadRightOffset.Value = r;
            }
            NOVRHeadsetData.CalibrateTranslation();
        }
        CameraStateManager.enableMouseLook = false;
    }

    private static string ResolveAircraftId(Aircraft aircraft)
    {
        if (aircraft == null || aircraft.definition == null) return null;
        return Regex.Replace(aircraft.definition.name, "[^a-zA-Z0-9_]", "_");
    }

}
