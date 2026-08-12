using UnityEngine;
using UnityEngine.Rendering.Universal;
using UnityEngine.UI;
using NOVR.VrCamera;

namespace NOVR.VrUi.Capture;

/// <summary>
/// Shows the base game's own helmet-mounted display in VR, unmodified.
///
/// The flat game has two symbology layers: the flight HUD, boresight-referenced
/// (captured onto the airframe panel by <see cref="FlightHudCaptureBackend"/>),
/// and <c>HeadMountedDisplay</c> — four minimal readouts (speed left, altitude
/// right, bearing and horizon up top) at fixed offsets from the view centre,
/// glued to wherever the pilot looks. In VR "glued to the view" means glued to
/// the head, so this backend puts that canvas on a head-locked panel: the visor.
///
/// **Why the canvas gets its own orthographic camera.** The HMD canvas is
/// ScreenSpaceOverlay, which is broken here twice over: the overlay capture
/// draws *every* overlay canvas, so the helmet readouts were riding the
/// airframe HUD panel; and the game's declutter (`HMD Hide Distance`) compares
/// pixel positions that stop being pixels the moment the canvas leaves screen
/// space. Retargeting the canvas to ScreenSpaceCamera on an orthographic
/// camera whose half-height equals half the texture height makes the canvas
/// scale exactly 1 — every widget position is a pixel again, like the flat
/// game — and removes it from the overlay set, so it leaves the HUD panel
/// automatically. The camera sits on an island far below the world and renders
/// only the canvas plane parked in front of it.
///
/// The declutter itself runs unmodified: <see cref="HarmonyPatches.HmdDeclutterPatch"/>
/// hands `HeadMountedDisplay.Update` the one input it needs — where the nose
/// is in the pilot's view, in visor pixels — and the game's own hide logic does
/// the rest, per widget, at the game's own `HMD Hide Distance` setting.
/// </summary>
public class HmdVisorBackend : NOVRBehaviour
{
    private const float PanelCanvasReferenceWidth = 1000f;
    private const float RebindInterval = 0.5f;
    private const float IslandY = -25000f;

    private static HmdVisorBackend? _instance;

    private Canvas? _hmdCanvas;
    private Camera? _visorCamera;
    private RenderTexture? _target;
    private Canvas? _panelCanvas;
    private RawImage? _panelImage;
    private RectTransform? _panelRect;
    private float _nextRebind;
    private bool _active;
    private bool _loggedPlacement;
    private int _targetWidth;
    private int _targetHeight;

    private RenderMode _savedRenderMode;
    private Camera? _savedWorldCamera;
    private float _savedPlaneDistance;

    public static bool IsVisorActive => _instance != null && _instance._active;

    /// <summary>
    /// Where the airframe boresight sits in the visor canvas's space — the one
    /// value the game's declutter needs. World coordinates on the pixel-true
    /// canvas, so distances to the widgets come out in pixels, exactly as the
    /// flat game computes them. Far away when the nose is behind the head, so
    /// nothing declutters while the pilot looks backwards.
    /// </summary>
    public static Vector3? NoseInVisorPixels()
    {
        var self = _instance;
        if (self == null || !self._active || self._hmdCanvas == null) return null;

        var canvasRect = (RectTransform)self._hmdCanvas.transform;
        var nose = Quaternion.Inverse(NOVRHeadsetData.Rotation) * Vector3.forward;
        if (nose.z < 0.05f) return canvasRect.TransformPoint(new Vector3(1e6f, 1e6f, 0f));

        var fov = Mathf.Clamp(CapturedHmd.VisorFieldOfView?.Value ?? 70f, 30f, 110f);
        var pxPerTan = self._targetWidth * 0.5f / Mathf.Tan(fov * 0.5f * Mathf.Deg2Rad);
        return canvasRect.TransformPoint(new Vector3(
            nose.x / nose.z * pxPerTan,
            nose.y / nose.z * pxPerTan,
            0f));
    }

    protected override void Awake()
    {
        base.Awake();
        _instance = this;
    }

    private void OnDestroy()
    {
        if (ReferenceEquals(_instance, this)) _instance = null;
        Teardown();
    }

    private void Update()
    {
        var want = FlightHudCaptureBackend.IsActive && (CapturedHmd.Visor?.Value ?? true);
        if (!want)
        {
            if (_active) Teardown();
            return;
        }

        if (_hmdCanvas == null && Time.unscaledTime >= _nextRebind)
        {
            _nextRebind = Time.unscaledTime + RebindInterval;
            _hmdCanvas = FindHmdCanvas();
        }

        if (_hmdCanvas == null || !_hmdCanvas.gameObject.activeInHierarchy)
        {
            if (_active) Teardown();
            return;
        }

        if (!_active) Setup();
        Maintain();
    }

    private static Canvas? FindHmdCanvas()
    {
        var hmd = SceneSingleton<HeadMountedDisplay>.i;
        if (hmd == null) return null;
        return hmd.GetComponent<Canvas>() ?? hmd.GetComponentInParent<Canvas>();
    }

    private void Setup()
    {
        // Pixel-for-pixel with the flat game's HMD layout, which is sized by
        // the game's own settings.
        _targetWidth = Mathf.Clamp(Mathf.RoundToInt(PlayerSettings.hmdWidth), 512, 4096);
        _targetHeight = Mathf.Clamp(Mathf.RoundToInt(PlayerSettings.hmdHeight), 256, 4096);

        _target = new RenderTexture(_targetWidth, _targetHeight, 0, RenderTextureFormat.ARGB32)
        {
            name = "NOVR HMD Visor Capture",
            filterMode = FilterMode.Bilinear,
            useMipMap = false,
            autoGenerateMips = false,
        };
        _target.Create();

        var go = new GameObject("NOVR HMD Visor Camera");
        go.transform.position = new Vector3(0f, IslandY, 0f);
        go.transform.rotation = Quaternion.identity;

        var camera = go.AddComponent<Camera>();
        camera.orthographic = true;
        // Half the texture height, so the ScreenSpaceCamera canvas lays out at
        // scale 1 — its coordinates are pixels, which is what the game's
        // declutter arithmetic assumes.
        camera.orthographicSize = _targetHeight * 0.5f;
        camera.nearClipPlane = 0.1f;
        camera.farClipPlane = 10f;
        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.backgroundColor = Color.clear;
        camera.targetTexture = _target;
        camera.depth = -99f;
        camera.stereoTargetEye = StereoTargetEyeMask.None;
        camera.allowHDR = false;
        camera.allowMSAA = false;
        camera.useOcclusionCulling = false;

        var data = go.AddComponent<UniversalAdditionalCameraData>();
        data.renderType = CameraRenderType.Base;
        data.renderPostProcessing = false;
        data.requiresColorOption = CameraOverrideOption.Off;
        data.requiresDepthOption = CameraOverrideOption.Off;

        VrCameraManager.IgnoredCameras.Add(camera);
        _visorCamera = camera;

        _savedRenderMode = _hmdCanvas!.renderMode;
        _savedWorldCamera = _hmdCanvas.worldCamera;
        _savedPlaneDistance = _hmdCanvas.planeDistance;
        _hmdCanvas.renderMode = RenderMode.ScreenSpaceCamera;
        _hmdCanvas.worldCamera = camera;
        _hmdCanvas.planeDistance = 1f;

        EnsurePanel();

        _active = true;
        Debug.Log($"[NOVR] HMD visor active: the game's helmet display on a head-locked panel, " +
                  $"{_targetWidth}x{_targetHeight} pixel-true capture.");
    }

    private void Teardown()
    {
        _active = false;

        if (_hmdCanvas != null)
        {
            _hmdCanvas.renderMode = _savedRenderMode;
            _hmdCanvas.worldCamera = _savedWorldCamera;
            _hmdCanvas.planeDistance = _savedPlaneDistance;
        }
        _hmdCanvas = null;

        if (_panelCanvas != null)
        {
            if (_panelImage != null && _panelImage.material != null) Destroy(_panelImage.material);
            Destroy(_panelCanvas.gameObject);
            _panelCanvas = null;
            _panelImage = null;
            _panelRect = null;
        }

        if (_visorCamera != null)
        {
            VrCameraManager.IgnoredCameras.Remove(_visorCamera);
            Destroy(_visorCamera.gameObject);
            _visorCamera = null;
        }

        if (_target != null)
        {
            _target.Release();
            Destroy(_target);
            _target = null;
        }
    }

    private void EnsurePanel()
    {
        if (_panelCanvas != null) return;

        var hudCamera = APIBus.CockpitHudCamera;
        if (hudCamera == null) return;

        var go = new GameObject("NOVR HMD Visor Panel");
        // Head-locked: the visor is glued to the helmet, so its parent is the
        // pose-driven UI camera. This is the placement the airframe HUD panel
        // deliberately does not use — the two panels are the two halves of the
        // original game's split.
        go.transform.SetParent(hudCamera.transform, false);

        var canvas = go.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        canvas.worldCamera = hudCamera;

        var rect = (RectTransform)canvas.transform;
        var imageGo = new GameObject("Visor Texture");
        imageGo.transform.SetParent(rect, false);

        var image = imageGo.AddComponent<RawImage>();
        image.texture = _target;
        image.raycastTarget = false;
        image.material = FlightHudCaptureBackend.CreatePanelMaterial();

        var imageRect = (RectTransform)imageGo.transform;
        imageRect.anchorMin = Vector2.zero;
        imageRect.anchorMax = Vector2.one;
        imageRect.offsetMin = Vector2.zero;
        imageRect.offsetMax = Vector2.zero;

        LayerHelper.SetLayerRecursive(go.transform, LayerHelper.GetVrUiLayer());

        _panelCanvas = canvas;
        _panelImage = image;
        _panelRect = rect;
    }

    private void Maintain()
    {
        // The canvas is the game's object; nothing stops game code or a scene
        // event from putting it back. Re-assert rather than assume.
        if (_hmdCanvas != null &&
            (_hmdCanvas.renderMode != RenderMode.ScreenSpaceCamera ||
             !ReferenceEquals(_hmdCanvas.worldCamera, _visorCamera)))
        {
            _hmdCanvas.renderMode = RenderMode.ScreenSpaceCamera;
            _hmdCanvas.worldCamera = _visorCamera;
            _hmdCanvas.planeDistance = 1f;
        }

        EnsurePanel();
        if (_panelRect == null) return;

        var distance = Mathf.Clamp(CapturedFlightHud.Distance?.Value ?? 25f, 2f, 200f);
        var fovDegrees = Mathf.Clamp(CapturedHmd.VisorFieldOfView?.Value ?? 70f, 30f, 110f);
        var widthMeters = 2f * distance * Mathf.Tan(fovDegrees * 0.5f * Mathf.Deg2Rad);

        if (_panelImage != null && _panelImage.material != null)
        {
            var brightness = Mathf.Clamp(CapturedFlightHud.Brightness?.Value ?? 2f, 0.25f, 8f);
            _panelImage.material.SetColor("_Color", new Color(brightness, brightness, brightness, 1f));
        }

        var aspect = (float)_targetWidth / _targetHeight;
        _panelRect.sizeDelta = new Vector2(PanelCanvasReferenceWidth, PanelCanvasReferenceWidth / aspect);
        var scale = widthMeters / PanelCanvasReferenceWidth;
        _panelRect.localScale = new Vector3(scale, scale, scale);
        _panelRect.localPosition = new Vector3(0f, 0f, distance);
        _panelRect.localRotation = Quaternion.identity;

        if (_loggedPlacement) return;
        _loggedPlacement = true;
        Debug.Log($"[NOVR] HMD visor panel: {widthMeters:F1} m wide at {distance:F1} m " +
                  $"({fovDegrees:F0}deg horizontal), head-locked.");
    }
}
