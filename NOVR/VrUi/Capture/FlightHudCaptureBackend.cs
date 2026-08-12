using UnityEngine;
using UnityEngine.Rendering.Universal;
using UnityEngine.UI;
using NOVR.VrCamera;

namespace NOVR.VrUi.Capture;

/// <summary>
/// Shows the game's own flight HUD in VR as a single flat panel fixed in the
/// cockpit, the way a real HUD combiner is fixed to the airframe.
///
/// The mod's normal path takes the HUD apart: the canvas is converted to world
/// space and every symbol — velocity vector, pipper, unit markers, threat
/// notches, pitch ladder — is re-projected individually, which is eight Harmony
/// patches and a per-element scale. This backend does the opposite. It leaves
/// the HUD in ScreenSpaceOverlay exactly as the base game authored it, renders
/// that overlay pass into a texture (the same machinery
/// <see cref="MenuCaptureBackend"/> uses for the menus), and puts the texture on
/// one quad. Nothing is re-projected, so nothing can be re-projected wrongly.
///
/// The trade is stated up front rather than discovered: a flat panel cannot
/// conform symbols to the world. The velocity vector marks where the flat HUD
/// put it, not where the aircraft is actually going as seen from the headset,
/// and a target marker sits where the mono camera projected it. It is a HUD you
/// read, not a HUD you aim with.
///
/// **Why the panel needs no anchoring code.** The NOVR root is created at
/// identity and never moved; <c>VrCockpitHudCamera</c> hangs off it with a
/// <see cref="NOVRPoseDriver"/> that writes the raw headset pose into its
/// *local* transform. The headset pose is measured against the play space,
/// which the game keeps attached to the cockpit — so the root's local space
/// already *is* the airframe's frame, and the camera moves inside it exactly as
/// the pilot's head moves inside the cockpit. A panel parked at a constant local
/// position under the root is therefore aircraft-fixed for free: it holds still
/// when you look around, rolls with the aircraft because you roll with it, and
/// shifts correctly when you lean. This is the same reason
/// <see cref="MenuCaptureBackend"/> has to work to *undo* that and follow the
/// head — for a HUD, the default is what we want.
///
/// Distance stands in for collimation. A real combiner projects at infinity, so
/// the symbology has no stereo disparity and no parallax against the head. A
/// quad a long way out is the cheap approximation: at the default 25 m the
/// residual disparity across a 64 mm IPD is about 2.6 arc-minutes, below what
/// the eye resolves, so it reads as projected rather than as a screen hanging in
/// the cockpit. Size is given as an angle rather than metres for the same
/// reason — what matters is how much of the view it covers, not how big it is.
/// </summary>
public class FlightHudCaptureBackend : NOVRBehaviour
{
    private const string HudCanvasName = "HUDCanvas";
    private const float PanelCanvasReferenceWidth = 1000f;
    private const float RebindInterval = 0.5f;
    // Same reason as the menu backend's: the HUD canvas is deactivated for a
    // frame at a time around aircraft changes and respawns, and tearing the
    // target down on the first inactive frame rebuilds it repeatedly.
    private const float TeardownGrace = 1.5f;

    private static FlightHudCaptureBackend? _instance;

    private Canvas? _hudCanvas;
    private Camera? _captureCamera;
    private RenderTexture? _target;
    private Canvas? _panelCanvas;
    private RawImage? _panelImage;
    private RectTransform? _panelRect;
    private float _nextRebind;
    private float _lastWantedCapture;
    private bool _capturing;
    private bool _loggedPlacement;
    private int _targetWidth;
    private int _targetHeight;

    /// <summary>True while this backend owns the overlay UI.</summary>
    public static bool IsActive => _instance != null && _instance._capturing;

    public static bool IsCaptureCamera(Camera? camera) =>
        camera != null && _instance != null && ReferenceEquals(camera, _instance._captureCamera);

    /// <summary>
    /// Gated on the setting alone. The setting also stops the flight-HUD patches
    /// being applied at all (see <see cref="VanillaFlightHud"/>), so a build with
    /// it off never reaches any of this.
    /// </summary>
    public static bool Enabled => CapturedFlightHud.Enabled?.Value ?? false;

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
        // One capture camera at a time: both backends draw *every* overlay
        // canvas, so running them together would put the menu on the HUD glass
        // and the HUD on the menu panel. The menus win — they are the ones
        // being interacted with.
        if (!Enabled || MenuCaptureBackend.IsActive)
        {
            if (_capturing) TeardownCapture();
            return;
        }

        if (Time.unscaledTime >= _nextRebind)
        {
            _nextRebind = Time.unscaledTime + RebindInterval;
            _hudCanvas = FindHudCanvas();
        }

        // ScreenSpaceOverlay is the load-bearing condition, not a sanity check:
        // it is what says the HUD really was left alone. If some other layer
        // converts the canvas after all, the overlay pass draws nothing and the
        // panel would hang there blank.
        var wantCapture = _hudCanvas != null &&
                          _hudCanvas.isActiveAndEnabled &&
                          _hudCanvas.renderMode == RenderMode.ScreenSpaceOverlay;

        if (wantCapture) _lastWantedCapture = Time.unscaledTime;

        if (wantCapture && !_capturing) SetupCapture();
        else if (!wantCapture && _capturing && Time.unscaledTime - _lastWantedCapture > TeardownGrace) TeardownCapture();

        if (_capturing) UpdatePanel();
    }

    private static Canvas? FindHudCanvas()
    {
        foreach (var canvas in Resources.FindObjectsOfTypeAll<Canvas>())
        {
            if (canvas == null) continue;
            if (canvas.name != HudCanvasName) continue;
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

        _capturing = true;
        Debug.Log($"[NOVR] Captured flight HUD active: rendering the overlay pass into a " +
                  $"{_targetWidth}x{_targetHeight} texture on a cockpit-fixed panel.");
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

        // The game's own resolution, so the canvas lays out exactly as it does
        // flat and the thin HUD strokes stay one pixel wide.
        _target = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32)
        {
            name = "NOVR Flight HUD Capture",
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

        var go = new GameObject("NOVR Flight HUD Capture Camera");
        go.transform.SetParent(transform, false);

        var camera = go.AddComponent<Camera>();
        // Draws nothing of the scene; it exists to give the capture pass a
        // target and a frame to run in.
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

        var go = new GameObject("NOVR Flight HUD Panel");
        // Parented to the NOVR root, which is the airframe's frame — see the
        // class comment. This is the entire placement mechanism.
        go.transform.SetParent(transform, false);

        var canvas = go.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        canvas.worldCamera = APIBus.CockpitHudCamera;

        var rect = (RectTransform)canvas.transform;
        var imageGo = new GameObject("HUD Texture");
        imageGo.transform.SetParent(rect, false);

        var image = imageGo.AddComponent<RawImage>();
        image.texture = _target;
        image.raycastTarget = false;
        image.material = CreatePanelMaterial();

        var imageRect = (RectTransform)imageGo.transform;
        imageRect.anchorMin = Vector2.zero;
        imageRect.anchorMax = Vector2.one;
        imageRect.offsetMin = Vector2.zero;
        imageRect.offsetMax = Vector2.zero;

        LayerHelper.SetLayerRecursive(go.transform, LayerHelper.GetVrUiLayer());

        _panelCanvas = canvas;
        _panelImage = image;
        _panelRect = rect;

        ApplyPlacement();
    }

    /// <summary>
    /// The panel adds light rather than compositing over the view, because that
    /// is what a combiner does — it is a half-silvered mirror and has no way to
    /// make the world behind it darker.
    ///
    /// This is not a cosmetic preference. The captured texture is the whole flat
    /// frame, and the game's HUD draws translucent backing panels behind the
    /// weapon status and the tactical map. Alpha-composited onto a quad those
    /// become solid grey rectangles hanging in the sky; added, their near-black
    /// contributes nothing and only the green strokes survive. Measured on the
    /// first harness run of this backend, which is what prompted it.
    ///
    /// <c>Unlit/AdditiveTextShader</c> is the game's own HUD text shader, so it
    /// is guaranteed to be in the build — the same reason
    /// <see cref="MotionControllerVisual"/> tries it first. If it ever is not,
    /// the fallback is the stock UI material: alpha-blended and ugly, but
    /// visible, which beats a magenta panel or none at all.
    /// </summary>
    private static Material? CreatePanelMaterial()
    {
        var shader = Shader.Find("Unlit/AdditiveTextShader");
        if (shader != null) return new Material(shader);

        Debug.LogWarning("[NOVR] Unlit/AdditiveTextShader not found; the captured HUD panel will " +
                         "be alpha-blended, so the game's translucent HUD backings will show as " +
                         "grey boxes.");
        return null;
    }

    /// <summary>
    /// Size and park the panel. Constant local transform under the root: no
    /// anchoring, no per-frame chase, nothing to go stale.
    /// </summary>
    private void ApplyPlacement()
    {
        if (_panelRect == null) return;

        var distance = Mathf.Clamp(CapturedFlightHud.Distance?.Value ?? 25f, 2f, 200f);
        var fovDegrees = Mathf.Clamp(CapturedFlightHud.FieldOfView?.Value ?? 60f, 20f, 120f);

        // Width from the angle it should subtend at that distance, so changing
        // the distance alone does not change how big the HUD looks — it only
        // changes how collimated it is.
        var widthMeters = 2f * distance * Mathf.Tan(fovDegrees * 0.5f * Mathf.Deg2Rad);

        var aspect = _targetHeight > 0 ? (float)_targetWidth / _targetHeight : 16f / 9f;
        _panelRect.sizeDelta = new Vector2(PanelCanvasReferenceWidth, PanelCanvasReferenceWidth / aspect);

        var scale = widthMeters / PanelCanvasReferenceWidth;
        _panelRect.localScale = new Vector3(scale, scale, scale);

        // Straight down the nose. The pilot's eye is not exactly at the root
        // origin, but at 25 m the difference is a fraction of a degree.
        _panelRect.localPosition = new Vector3(0f, 0f, distance);
        _panelRect.localRotation = Quaternion.identity;

        if (_loggedPlacement) return;
        _loggedPlacement = true;
        Debug.Log($"[NOVR] Flight HUD panel: {widthMeters:F1} m wide at {distance:F1} m " +
                  $"({fovDegrees:F0}deg horizontal), fixed in the cockpit frame.");
    }

    private void UpdatePanel()
    {
        // Idempotent: a scene load can take the rig with it while the capture is
        // still inside its teardown grace.
        EnsureTarget();
        EnsureCaptureCamera();
        EnsurePanel();

        if (_panelImage != null && _panelImage.texture != _target) _panelImage.texture = _target;
        ApplyPlacement();
    }
}
