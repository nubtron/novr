using UnityEngine;
using UnityEngine.Rendering.Universal;
using UnityEngine.UI;
using NOVR.VrCamera;

namespace NOVR.VrUi.Capture;

/// <summary>
/// Alternative backend for the game's own menus: instead of converting their
/// canvases to world space, leave them in ScreenSpaceOverlay, render them into
/// a texture through the engine's overlay path, and show that texture on a
/// panel in front of the player.
///
/// The world-space conversion is what breaks the game's UI in VR — it silently
/// drops three guarantees the UI was authored against (draw order follows the
/// hierarchy, local z is ignored, layers and cameras do not matter), and each
/// lost guarantee has needed its own patch: masked text disappearing behind a
/// stencil pop, list items ordered by z, dropdown popups on the wrong layer.
/// Capturing the overlay pass keeps all three, so those failure modes cannot
/// occur here.
///
/// The flight HUD deliberately stays on the world-space path: it is projected
/// into the cockpit and scaled per element, which a flat panel cannot do.
/// </summary>
public class MenuCaptureBackend : NOVRBehaviour
{
    private const string MenuCanvasName = "MainCanvas";
    private const float PanelCanvasReferenceWidth = 1000f;
    private const float RebindInterval = 0.5f;
    // The menu canvas is deactivated for a frame at a time by the behaviour
    // patcher and during scene transitions. Tearing the capture down on the
    // first inactive frame meant rebuilding the target, camera and panel three
    // times in one menu visit, so an absence has to persist to count.
    private const float TeardownGrace = 1.5f;

    private static MenuCaptureBackend? _instance;

    private Canvas? _menuCanvas;
    private Camera? _captureCamera;
    private RenderTexture? _target;
    private Canvas? _panelCanvas;
    private RawImage? _panelImage;
    private RectTransform? _panelRect;
    private Vector3 _anchorPosition;
    private Quaternion _anchorRotation = Quaternion.identity;
    private bool _anchorInitialized;
    private bool _loggedPlacement;
    private float _nextRebind;
    private float _lastWantedCapture;
    private bool _capturing;
    private bool _loggedPassEnqueued;
    private bool _loggedHookFailure;
    private int _targetWidth;
    private int _targetHeight;

    /// <summary>True while the captured-menu backend owns the overlay UI.</summary>
    public static bool IsActive => _instance != null && _instance._capturing;

    /// <summary>
    /// Whether URP should skip drawing overlay canvases into the eye buffers.
    /// Only while capturing, so a build with the backend off — or a session
    /// that never reaches a menu — renders exactly as before.
    /// </summary>
    public static bool SuppressesScreenOverlayUi => IsActive;

    /// <summary>
    /// The backend replaces the game's menus, so it stands down when NOVR's own
    /// native menu UI is the one being shown.
    /// </summary>
    public static bool Enabled =>
        ModConfiguration.Instance.CaptureMenusToTexture.Value &&
        !ModConfiguration.Instance.EnableNativeMenuUi.Value;

    public static bool IsCaptureCamera(Camera? camera) =>
        camera != null && _instance != null && ReferenceEquals(camera, _instance._captureCamera);

    protected override void Awake()
    {
        base.Awake();
        _instance = this;
    }

    private void OnDestroy()
    {
        if (ReferenceEquals(_instance, this)) _instance = null;
        TeardownCapture();
    }

    private void Update()
    {
        if (!Enabled)
        {
            if (_capturing) TeardownCapture();
            return;
        }

        if (Time.unscaledTime >= _nextRebind)
        {
            _nextRebind = Time.unscaledTime + RebindInterval;
            _menuCanvas = FindMenuCanvas();
        }

        var wantCapture = _menuCanvas != null &&
                          _menuCanvas.isActiveAndEnabled &&
                          _menuCanvas.renderMode == RenderMode.ScreenSpaceOverlay;

        if (wantCapture) _lastWantedCapture = Time.unscaledTime;

        if (wantCapture && !_capturing) SetupCapture();
        else if (!wantCapture && _capturing && Time.unscaledTime - _lastWantedCapture > TeardownGrace) TeardownCapture();

        if (_capturing) UpdatePanel();
    }

    private static Canvas? FindMenuCanvas()
    {
        foreach (var canvas in Resources.FindObjectsOfTypeAll<Canvas>())
        {
            if (canvas == null) continue;
            if (canvas.name != MenuCanvasName) continue;
            if (!canvas.gameObject.scene.IsValid()) continue;
            return canvas;
        }

        return null;
    }

    private void SetupCapture()
    {
        EnsureTarget();
        EnsureCaptureCamera();
        EnsurePanel();
        _anchorInitialized = false;
        RecenterPanel();

        _capturing = true;
        Debug.Log($"[NOVR] Captured-menu backend active: rendering '{MenuCanvasName}' " +
                  $"through the overlay path into a {_targetWidth}x{_targetHeight} texture.");
    }

    private void TeardownCapture()
    {
        _capturing = false;

        if (_panelCanvas != null)
        {
            Destroy(_panelCanvas.gameObject);
            _panelCanvas = null;
            _panelImage = null;
            _panelRect = null;
        }

        if (_captureCamera != null)
        {
            VrCameraManager.IgnoredCameras.Remove(_captureCamera);
            Destroy(_captureCamera.gameObject);
            _captureCamera = null;
        }

        if (_target != null)
        {
            _target.Release();
            Destroy(_target);
            _target = null;
        }
    }

    private void EnsureTarget()
    {
        var width = Mathf.Max(640, Screen.width);
        var height = Mathf.Max(480, Screen.height);

        if (_target != null && _targetWidth == width && _targetHeight == height) return;

        if (_target != null)
        {
            _target.Release();
            Destroy(_target);
        }

        // Matching the game's own resolution keeps the canvas layout identical
        // to the flat one and the text as crisp as the source.
        _target = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32)
        {
            name = "NOVR Menu Capture",
            filterMode = FilterMode.Bilinear,
            useMipMap = false,
            autoGenerateMips = false,
        };
        _target.Create();
        _targetWidth = width;
        _targetHeight = height;

        if (_captureCamera != null) _captureCamera.targetTexture = _target;
        if (_panelImage != null) _panelImage.texture = _target;
    }

    private void EnsureCaptureCamera()
    {
        if (_captureCamera != null) return;

        var go = new GameObject("NOVR Menu Capture Camera");
        go.transform.SetParent(transform, false);

        var camera = go.AddComponent<Camera>();
        // Nothing in the scene is drawn: this camera exists purely to give the
        // capture pass a render target and a frame to run in.
        camera.cullingMask = 0;
        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.backgroundColor = Color.clear;
        camera.targetTexture = _target;
        camera.depth = -100f;
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
        _captureCamera = camera;
    }

    private void EnsurePanel()
    {
        if (_panelCanvas != null) return;

        var go = new GameObject("NOVR Menu Panel");
        // The menus live in their own scenes (the mission picker is a scene
        // load away from the main menu), so a plain scene object is destroyed
        // out from under the backend on the way in.
        DontDestroyOnLoad(go);
        var canvas = go.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        canvas.worldCamera = APIBus.CockpitHudCamera;

        var rect = (RectTransform)canvas.transform;
        var imageGo = new GameObject("Menu Texture");
        imageGo.transform.SetParent(rect, false);

        var image = imageGo.AddComponent<RawImage>();
        image.texture = _target;
        image.raycastTarget = false;

        var imageRect = (RectTransform)imageGo.transform;
        imageRect.anchorMin = Vector2.zero;
        imageRect.anchorMax = Vector2.one;
        imageRect.offsetMin = Vector2.zero;
        imageRect.offsetMax = Vector2.zero;

        LayerHelper.SetLayerRecursive(go.transform, LayerHelper.GetVrUiLayer());

        _panelCanvas = canvas;
        _panelImage = image;
        _panelRect = rect;

        ApplyPanelSize();
    }

    private void ApplyPanelSize()
    {
        if (_panelRect == null) return;

        var aspect = _targetHeight > 0 ? (float)_targetWidth / _targetHeight : 16f / 9f;
        _panelRect.sizeDelta = new Vector2(PanelCanvasReferenceWidth, PanelCanvasReferenceWidth / aspect);

        var widthMeters = Mathf.Clamp(ModConfiguration.Instance.CapturedMenuWidth.Value, 0.5f, 8f);
        var scale = widthMeters / PanelCanvasReferenceWidth;
        _panelRect.localScale = new Vector3(scale, scale, scale);
    }

    private void UpdatePanel()
    {
        // Idempotent rebuilds: a scene load can still take the rig with it, and
        // the capture is only torn down after a grace period, so recovery
        // cannot wait for the next Setup.
        EnsureTarget();
        EnsureCaptureCamera();
        EnsurePanel();

        if (_panelImage != null && _panelImage.texture != _target) _panelImage.texture = _target;
        ApplyPanelSize();
        ApplyAnchor();
    }

    /// <summary>
    /// Park the panel in front of the headset, upright and facing the player.
    ///
    /// Placement is anchored rather than continuous — a panel that chases the
    /// head cannot be pointed at — but the anchor is re-applied every frame,
    /// because the reference pose is still settling when a menu first appears
    /// and the world origin shifts underneath long sessions. Placing once and
    /// walking away leaves the panel stranded outside the view.
    /// </summary>
    public void RecenterPanel()
    {
        CaptureAnchor();
        ApplyAnchor();
    }

    private void CaptureAnchor()
    {
        var reference = APIBus.CockpitHudReference;
        if (reference == null) return;

        var forward = Vector3.ProjectOnPlane(reference.transform.forward, Vector3.up);
        if (forward.sqrMagnitude < 0.0001f) forward = Vector3.forward;
        forward.Normalize();

        _anchorPosition = reference.transform.position;
        _anchorRotation = Quaternion.LookRotation(forward, Vector3.up);
        _anchorInitialized = true;
    }

    private void ApplyAnchor()
    {
        if (_panelRect == null) return;
        if (!_anchorInitialized) CaptureAnchor();
        if (!_anchorInitialized) return;

        var distance = Mathf.Clamp(ModConfiguration.Instance.CapturedMenuDistance.Value, 1f, 6f);

        // Only the facing is anchored. The origin has to be read live: the
        // reference pose is at the world origin for the first seconds of a
        // session and only then moves to wherever the menu camera actually is,
        // so a position captured once leaves the panel stranded in empty space
        // — which reads as "the backend renders nothing".
        var reference = APIBus.CockpitHudReference;
        var origin = reference != null ? reference.transform.position : _anchorPosition;
        var position = origin + _anchorRotation * Vector3.forward * distance;

        _panelRect.position = position;
        _panelRect.rotation = _anchorRotation;

        if (_loggedPlacement || origin == Vector3.zero) return;
        _loggedPlacement = true;
        Debug.Log($"[NOVR] Captured menu panel placed at {position} facing {_anchorRotation.eulerAngles} " +
                  $"(reference at {origin}).");
    }

    public static void Recenter() => _instance?.RecenterPanel();

    /// <summary>
    /// Map a world point on (or near) the panel back to the screen coordinate
    /// the overlay canvases were rendered at, which is what the virtual mouse
    /// and every GraphicRaycaster in the game expect.
    /// </summary>
    public static bool TryGetScreenPoint(Vector3 worldPoint, out Vector2 screenPoint)
    {
        screenPoint = Vector2.zero;
        if (!IsActive || _instance?._panelRect == null) return false;

        var rect = _instance._panelRect;
        var local = rect.InverseTransformPoint(worldPoint);
        var size = rect.rect.size;
        if (size.x <= 0f || size.y <= 0f) return false;

        // Rect pivot is centred, so shift into 0..1 before scaling to pixels.
        var normalized = new Vector2(local.x / size.x + 0.5f, local.y / size.y + 0.5f);
        screenPoint = new Vector2(
            Mathf.Clamp(normalized.x * Screen.width, 0f, Screen.width),
            Mathf.Clamp(normalized.y * Screen.height, 0f, Screen.height));
        return true;
    }

    /// <summary>Distance from a ray origin to the panel plane, so the cursor
    /// can sit on the panel rather than at its default projection distance.</summary>
    public static bool TryGetPanelDistance(Vector3 origin, Vector3 direction, out float distance)
    {
        distance = 0f;
        if (!IsActive || _instance?._panelRect == null) return false;

        var rect = _instance._panelRect;
        var plane = new Plane(-rect.forward, rect.position);
        if (!plane.Raycast(new Ray(origin, direction), out var hit) || hit <= 0f) return false;

        distance = hit;
        return true;
    }

    internal static void NotifyCapturePassEnqueued()
    {
        if (_instance == null || _instance._loggedPassEnqueued) return;
        _instance._loggedPassEnqueued = true;
        Debug.Log("[NOVR] Overlay UI capture pass enqueued on the capture camera.");
    }

    internal static void ReportRenderHookFailure(string reason)
    {
        if (_instance == null || _instance._loggedHookFailure) return;
        _instance._loggedHookFailure = true;
        // Silence here would look like "the menu is just black", which is the
        // one failure this backend must never present without a reason.
        Debug.LogWarning($"[NOVR] Captured-menu backend cannot inject its render pass: {reason}. " +
                         "Falling back to nothing being drawn; turn off Capture Menus To Texture.");
    }
}
