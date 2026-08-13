using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using UnityEngine.UI;
using NOVR.VrCamera;
using NOVR.VrUi.SpecialBehavior;

namespace NOVR.VrUi.Capture;

/// <summary>
/// The base game's screen-space icon layer — unit markers, the target
/// designator, the selected-target edge arrow, radar warnings, missile notch
/// cues and the objective pointer — head-locked in VR, exactly as the flat
/// game glues it to the screen.
///
/// **What the flat game does.** All of these live on one full-screen canvas
/// and are re-projected every frame through <c>mainCamera.WorldToScreenPoint</c>;
/// the screen both *is* the pilot's whole field of view and follows it, so
/// icons can appear anywhere the pilot looks. The selected target is never
/// lost: off screen, <c>HUDFunctions.PinToScreenEdge</c> replaces its diamond
/// with an arrow pinned to the screen edge. The designator is an image no code
/// ever moves — screen centre *is* the look direction.
///
/// **What went wrong in VR.** The captured flight HUD assigns the whole
/// <c>HUDCanvas</c> to the airframe-fixed panel, so these view-referenced
/// elements were welded to the nose: look away and they are gone, and there is
/// no screen edge for the arrow because the panel edge is not the edge of what
/// the pilot can see.
///
/// **This backend restores the flat game's screen** — as a virtual one. A
/// 1920x1080 pixel-true canvas rides an island camera (the visor trick), shown
/// on a head-locked panel spanning the visor field of view; the game's icon
/// subtrees are reparented into it. A disabled head-posed camera with the
/// panel's exact frustum is what the icon code projects through (see
/// <c>HarmonyPatches.ViewLayerPatches</c>), so <c>WorldToScreenPoint</c>, the
/// 100 px selection radius and the edge-pin arithmetic all run unmodified —
/// the "screen edge" the arrow pins to is the visor rect, comfortably inside
/// what the eye can reach, by construction.
///
/// **Why the island is pinned at the world origin's zero.** The game's code
/// writes *absolute world positions* that are numerically screen pixels; the
/// canvas plane must therefore keep a fixed world pose. The rig lives in the
/// active scene (so scene lifetime works like every other capture) with a
/// <see cref="PositionZeroBehavior"/>, which the floating-origin patch resets
/// on every origin shift — the same arrangement the menu capture uses. The
/// island camera sits a child step below at y = -26000, one metre behind its
/// canvas, so the canvas plane lands exactly on local z = 0: several game
/// paths write positions through <c>Vector2</c>, and a plane at z = 0 is what
/// makes the dropped z coordinate land *on* the canvas instead of beside it.
/// </summary>
public class ViewLayerBackend : NOVRBehaviour
{
    public const int TexWidth = 1920;
    public const int TexHeight = 1080;

    private const float PanelCanvasReferenceWidth = 1000f;
    private const float RebindInterval = 0.5f;
    private const float IslandY = -26000f;

    private static readonly FieldInfo? TargetArrowField =
        AccessTools.Field(typeof(CombatHUD), "targetArrow");
    private static readonly FieldInfo? TargetTextField =
        AccessTools.Field(typeof(CombatHUD), "targetText");
    private static readonly FieldInfo? TargetInfoField =
        AccessTools.Field(typeof(CombatHUD), "targetInfo");

    private static ViewLayerBackend? _instance;

    private readonly struct CapturedSubtree
    {
        public readonly Transform Transform;
        public readonly Transform Parent;
        public readonly int SiblingIndex;

        public CapturedSubtree(Transform transform)
        {
            Transform = transform;
            Parent = transform.parent;
            SiblingIndex = transform.GetSiblingIndex();
        }
    }

    private readonly List<CapturedSubtree> _captured = new();

    private GameObject? _rig;
    private Camera? _islandCamera;
    private Canvas? _canvas;
    private RenderTexture? _target;
    private Camera? _projectionCamera;
    private Matrix4x4 _projectionMatrix = Matrix4x4.identity;
    private Canvas? _panelCanvas;
    private RawImage? _panelImage;
    private RectTransform? _panelRect;
    private float _nextRebind;
    private bool _active;
    private bool _loggedPlacement;

    public static bool IsActive => _instance != null && _instance._active;

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
        var combatHud = SceneSingleton<CombatHUD>.i;
        var want = FlightHudCaptureBackend.IsActive &&
                   (CapturedHmd.ViewIcons?.Value ?? true) &&
                   combatHud != null &&
                   combatHud.iconLayer != null &&
                   APIBus.CockpitHudCamera != null;

        if (!want)
        {
            if (_active) Teardown();
            return;
        }

        if (!_active)
        {
            if (Time.unscaledTime < _nextRebind) return;
            _nextRebind = Time.unscaledTime + RebindInterval;
            Setup(combatHud!);
        }

        Maintain();
    }

    /// <summary>
    /// The head eye: the disabled camera the icon code projects through while
    /// this layer is active. Its pixel rect is the capture texture (1920x1080)
    /// and its frustum is the panel's, so screen pixels map to view directions
    /// exactly. Re-asserts the projection matrix on every acquisition —
    /// <c>fieldOfView</c> alone is unreliable under XR (measured on the design
    /// eye: assigned 35.98, read back 60.00). Null whenever the swap must not
    /// happen.
    /// </summary>
    public static Camera? AcquireProjectionCamera()
    {
        var self = _instance;
        if (self == null || !self._active || self._projectionCamera == null) return null;

        self._projectionCamera.projectionMatrix = self._projectionMatrix;
        return self._projectionCamera;
    }

    /// <summary>
    /// Re-express a freshly written screen-pixel position in the island
    /// canvas's world space. The canvas is pixel-true (scale 1), so this is a
    /// pure translation: distances stay pixels, which is what the game's
    /// selection radius and declutter arithmetic assume.
    /// </summary>
    public static Vector3 RemapPixels(Vector3 screenPixels)
    {
        var self = _instance;
        if (self == null || !self._active || self._canvas == null) return screenPixels;

        var rect = (RectTransform)self._canvas.transform;
        return rect.TransformPoint(new Vector3(
            screenPixels.x - TexWidth * 0.5f,
            screenPixels.y - TexHeight * 0.5f,
            0f));
    }

    /// <summary>
    /// Where the airframe boresight sits in view pixels — the input for the
    /// designator's own self-hide (it fades out near the HUD centre in the
    /// flat game). Far away when the nose is behind the head.
    /// </summary>
    public static Vector2 NoseInViewPixels()
    {
        var self = _instance;
        if (self == null || !self._active) return new Vector2(1e6f, 1e6f);

        var nose = Quaternion.Inverse(NOVRHeadsetData.Rotation) * Vector3.forward;
        if (nose.z < 0.05f) return new Vector2(1e6f, 1e6f);

        var fov = Mathf.Clamp(CapturedHmd.VisorFieldOfView?.Value ?? 70f, 30f, 110f);
        var pxPerTan = TexWidth * 0.5f / Mathf.Tan(fov * 0.5f * Mathf.Deg2Rad);
        return new Vector2(
            TexWidth * 0.5f + nose.x / nose.z * pxPerTan,
            TexHeight * 0.5f + nose.y / nose.z * pxPerTan);
    }

    private void Setup(CombatHUD combatHud)
    {
        _target = new RenderTexture(TexWidth, TexHeight, 0, RenderTextureFormat.ARGB32)
        {
            name = "NOVR View Layer Capture",
            filterMode = FilterMode.Bilinear,
            useMipMap = false,
            autoGenerateMips = false,
        };
        _target.Create();

        // Root in the active scene, pinned at zero across origin shifts. The
        // camera hangs below it so the canvas plane sits exactly on z = 0.
        _rig = new GameObject("NOVR View Layer Rig");
        _rig.AddComponent<PositionZeroBehavior>();

        var camGo = new GameObject("NOVR View Layer Camera");
        camGo.transform.SetParent(_rig.transform, false);
        camGo.transform.localPosition = new Vector3(0f, IslandY, -1f);
        camGo.transform.localRotation = Quaternion.identity;

        var camera = camGo.AddComponent<Camera>();
        camera.orthographic = true;
        // Half the texture height: the ScreenSpaceCamera canvas lays out at
        // scale 1, so its world coordinates are pixels (offset to the island).
        camera.orthographicSize = TexHeight * 0.5f;
        camera.nearClipPlane = 0.1f;
        camera.farClipPlane = 10f;
        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.backgroundColor = Color.clear;
        camera.targetTexture = _target;
        camera.depth = -98f;
        camera.stereoTargetEye = StereoTargetEyeMask.None;
        camera.allowHDR = false;
        camera.allowMSAA = false;
        camera.useOcclusionCulling = false;

        var data = camGo.AddComponent<UniversalAdditionalCameraData>();
        data.renderType = CameraRenderType.Base;
        data.renderPostProcessing = false;
        data.requiresColorOption = CameraOverrideOption.Off;
        data.requiresDepthOption = CameraOverrideOption.Off;

        VrCameraManager.IgnoredCameras.Add(camera);
        _islandCamera = camera;

        var canvasGo = new GameObject("NOVR View Layer Canvas");
        canvasGo.transform.SetParent(_rig.transform, false);
        var canvas = canvasGo.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceCamera;
        canvas.worldCamera = camera;
        canvas.planeDistance = 1f;
        _canvas = canvas;

        CaptureSubtrees(combatHud, canvas.transform);
        EnsureProjectionCamera();
        EnsurePanel();

        _active = true;
        Debug.Log($"[NOVR] View layer active: the game's screen-space icons on a head-locked " +
                  $"{TexWidth}x{TexHeight} virtual screen.");
    }

    private void CaptureSubtrees(CombatHUD combatHud, Transform canvasTransform)
    {
        _captured.Clear();

        Capture(combatHud.iconLayer, canvasTransform);
        Capture(combatHud.targetDesignator != null ? combatHud.targetDesignator.transform : null, canvasTransform);
        Capture((TargetArrowField?.GetValue(combatHud) as Image)?.transform, canvasTransform);
        Capture((TargetTextField?.GetValue(combatHud) as Component)?.transform, canvasTransform);
        Capture((TargetInfoField?.GetValue(combatHud) as Component)?.transform, canvasTransform);

        // The icon layer spans the whole screen in the flat game; make it span
        // the whole virtual one. Its children are position-driven either way.
        if (combatHud.iconLayer is { } iconLayer && iconLayer.transform is RectTransform iconRect)
        {
            iconRect.anchorMin = Vector2.zero;
            iconRect.anchorMax = Vector2.one;
            iconRect.offsetMin = Vector2.zero;
            iconRect.offsetMax = Vector2.zero;
        }

        // The designator is the element no code ever moves: park it at the
        // centre of the virtual screen, which is the look direction — the flat
        // game's screen centre, restored rather than simulated.
        if (combatHud.targetDesignator != null &&
            combatHud.targetDesignator.transform is RectTransform designatorRect)
        {
            designatorRect.anchorMin = new Vector2(0.5f, 0.5f);
            designatorRect.anchorMax = new Vector2(0.5f, 0.5f);
            designatorRect.anchoredPosition3D = Vector3.zero;
        }
    }

    private void Capture(Transform? subtree, Transform canvasTransform)
    {
        if (subtree == null) return;
        _captured.Add(new CapturedSubtree(subtree));
        subtree.SetParent(canvasTransform, false);
    }

    private void EnsureProjectionCamera()
    {
        var mount = APIBus.CockpitHudCamera;
        if (mount == null) return;

        if (_projectionCamera == null)
        {
            var go = new GameObject("NOVR View Layer Eye");
            var camera = go.AddComponent<Camera>();
            camera.enabled = false;
            camera.cullingMask = 0;
            // Never renders; the texture only gives WorldToScreenPoint its
            // 1920x1080 pixel rect.
            camera.targetTexture = _target;
            camera.stereoTargetEye = StereoTargetEyeMask.None;
            camera.nearClipPlane = 0.1f;
            camera.farClipPlane = 50000f;
            VrCameraManager.IgnoredCameras.Add(camera);
            _projectionCamera = camera;
        }

        var t = _projectionCamera.transform;
        if (t.parent != mount.transform) t.SetParent(mount.transform, false);
        t.localPosition = Vector3.zero;
        t.localRotation = Quaternion.identity;

        var fovDegrees = Mathf.Clamp(CapturedHmd.VisorFieldOfView?.Value ?? 70f, 30f, 110f);
        var aspect = (float)TexWidth / TexHeight;
        var verticalFov =
            2f * Mathf.Atan(Mathf.Tan(fovDegrees * 0.5f * Mathf.Deg2Rad) / aspect) * Mathf.Rad2Deg;
        // The property too, not just the matrix: ObjectiveOverlay reads
        // mainCamera.fieldOfView for its size indicator.
        _projectionCamera.fieldOfView = verticalFov;
        _projectionMatrix = Matrix4x4.Perspective(verticalFov, aspect, 0.1f, 50000f);
        _projectionCamera.projectionMatrix = _projectionMatrix;
    }

    private void EnsurePanel()
    {
        if (_panelCanvas != null) return;

        var hudCamera = APIBus.CockpitHudCamera;
        if (hudCamera == null) return;

        var go = new GameObject("NOVR View Layer Panel");
        // Head-locked, like the visor: this layer is the half of the flat
        // game's screen that follows the look.
        go.transform.SetParent(hudCamera.transform, false);

        var canvas = go.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        canvas.worldCamera = hudCamera;

        var rect = (RectTransform)canvas.transform;
        var imageGo = new GameObject("View Layer Texture");
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
        // The subtrees are the game's objects: a scene load or an aircraft
        // teardown can take them away. Stand down and rebind rather than hold
        // stale references.
        if (_canvas == null || _rig == null ||
            SceneSingleton<CombatHUD>.i == null ||
            SceneSingleton<CombatHUD>.i.iconLayer == null ||
            SceneSingleton<CombatHUD>.i.iconLayer.parent != _canvas.transform)
        {
            Teardown();
            return;
        }

        EnsureProjectionCamera();
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

        var aspect = (float)TexWidth / TexHeight;
        _panelRect.sizeDelta = new Vector2(PanelCanvasReferenceWidth, PanelCanvasReferenceWidth / aspect);
        var scale = widthMeters / PanelCanvasReferenceWidth;
        _panelRect.localScale = new Vector3(scale, scale, scale);
        _panelRect.localPosition = new Vector3(0f, 0f, distance);
        _panelRect.localRotation = Quaternion.identity;

        if (_loggedPlacement) return;
        _loggedPlacement = true;
        Debug.Log($"[NOVR] View layer panel: {widthMeters:F1} m wide at {distance:F1} m " +
                  $"({fovDegrees:F0}deg horizontal), head-locked.");
    }

    private void Teardown()
    {
        _active = false;

        foreach (var entry in _captured)
        {
            if (entry.Transform == null || entry.Parent == null) continue;
            entry.Transform.SetParent(entry.Parent, false);
            entry.Transform.SetSiblingIndex(entry.SiblingIndex);
        }
        _captured.Clear();

        if (_panelCanvas != null)
        {
            if (_panelImage != null && _panelImage.material != null) Destroy(_panelImage.material);
            Destroy(_panelCanvas.gameObject);
            _panelCanvas = null;
            _panelImage = null;
            _panelRect = null;
        }

        if (_projectionCamera != null)
        {
            VrCameraManager.IgnoredCameras.Remove(_projectionCamera);
            Destroy(_projectionCamera.gameObject);
            _projectionCamera = null;
        }

        if (_islandCamera != null)
        {
            VrCameraManager.IgnoredCameras.Remove(_islandCamera);
            _islandCamera = null;
        }

        if (_rig != null)
        {
            Destroy(_rig);
            _rig = null;
        }
        _canvas = null;

        if (_target != null)
        {
            _target.Release();
            Destroy(_target);
            _target = null;
        }
    }
}
