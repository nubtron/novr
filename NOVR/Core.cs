using System;
using System.Reflection;
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

    /// <summary>
    /// Set once the mod has given up on XR, so <see cref="OnDestroy"/> lets the
    /// object go instead of resurrecting it. Static because the resurrection is
    /// what we are suppressing: the instance is on its way out.
    /// </summary>
    private static bool _stoodDown;

    private void OnDestroy()
    {
        if (_stoodDown) return;

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
        
        // XR comes up first, before anything is built on top of it. It is the
        // one step here that can fail for a reason outside the mod's control —
        // no headset, runtime not running, form factor unavailable — and it
        // used to throw straight out of Start(), from *below* the VR UI. That
        // left the mod half-built and the game unplayable: patched, UI alive,
        // menu clicks routed to a cursor with no head to drive it.
        //
        // Ordering it first is what makes giving up clean. Neither of the two
        // behaviours below reads XR state in Awake, so the working path is
        // unchanged; the failing path simply never constructs them, instead of
        // constructing them and hoping a pending Start() can be outrun.
        try
        {
            _vrTogglerManager = new VrTogglerManager();
        }
        catch (Exception ex)
        {
            StandDown(ex);
            return;
        }

        _headsetData = NOVRBehaviour.Create<NOVRHeadsetData>(transform);
        _vrUi = NOVRBehaviour.Create<NOUIManager>(transform);

    }

    /// <summary>
    /// Tear the mod back out of a running game after XR failed to start.
    /// Destroying this GameObject takes the whole mod with it: every behaviour
    /// added in <see cref="Awake"/> lives on it, and the VR UI and headset data
    /// are parented under its transform.
    ///
    /// This is only clean because it happens in Start(), before any of those
    /// behaviours have had an Update(): <c>VrCameraManager</c> has not yet
    /// reparented a camera, and the VR UI — the part that creates the
    /// VirtualMouse and rewrites the UI bindings — was never constructed at
    /// all. Nothing has to be undone because nothing has been done.
    /// </summary>
    private void StandDown(Exception cause)
    {
        Debug.LogWarning($"[NOVR] XR failed to start: {cause.Message}");

        _stoodDown = true;
        NOVRPlugin.StandDown("no XR runtime available (is the headset on and SteamVR running?).");
        Destroy(gameObject);
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
        if (_aircraft != _oldAircraft) NOVRHeadsetData.CalibrateTranslation();
        CameraStateManager.enableMouseLook = false;
    }

}
