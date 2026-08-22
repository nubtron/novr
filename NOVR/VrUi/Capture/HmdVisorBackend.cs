using System.Collections.Generic;
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
/// **Why the subtree moves to our own canvas.** The HMD is not a canvas of its
/// own — <c>HeadMountedDisplay</c> is a rect *inside* <c>HUDCanvas</c>
/// (measured: retargeting its parent canvas dragged the whole flight HUD to
/// the island). So its subtree is reparented into a canvas this backend owns:
/// ScreenSpaceCamera on an orthographic camera whose half-height equals half
/// the texture height, which makes the canvas scale exactly 1 — every widget
/// position is a pixel, the same arithmetic the game's declutter
/// (`HMD Hide Distance`) assumes. Moving it out of <c>HUDCanvas</c> also takes
/// the readouts off the airframe HUD panel, where the overlay capture had been
/// smearing them. The camera sits on an island far below the world; teardown
/// puts the subtree back where the scene had it, same parent, same sibling
/// index.
///
/// The declutter itself runs unmodified: <see cref="HarmonyPatches.HmdDeclutterPatch"/>
/// hands `HeadMountedDisplay.Update` the one input it needs — where the nose
/// is in the pilot's view, in visor pixels — and the game's own hide logic does
/// the rest, per widget, at the game's own `HMD Hide Distance` setting.
///
/// **The HMD rect is not all helmet, though.** Two of its children — the
/// tactical map and the weapon/countermeasure readout — are large, dense
/// displays rather than the four sparse readouts the helmet layer is named
/// after, and the flat game only puts them there because on a screen there is
/// nowhere else. Head-locked they ride the view wherever it goes, which is what
/// `Captured HMD Panel Lock` (on by default) takes back: those two are captured
/// separately, on their own island camera, onto a second panel parked in the
/// airframe's frame the way <see cref="FlightHudCaptureBackend"/>'s is. The
/// four readouts stay on the visor — they are what the helmet display is for,
/// and they are the only widgets the game's declutter and
/// <c>RefreshSettings</c> place, so leaving them is also leaving the game's own
/// arrangement alone. Decompiled to confirm the split is clean: `Update` hides
/// and `RefreshSettings` positions exactly `altitude`, `speed`, `bearing` and
/// `horizon`, and neither touches the map or the readout.
/// </summary>
public class HmdVisorBackend : NOVRBehaviour
{
    private const float PanelCanvasReferenceWidth = 1000f;
    private const float RebindInterval = 0.5f;
    private const float IslandY = -25000f;
    // A second island, far enough from the first that neither ortho camera can
    // see the other's canvas: both are created with the default culling mask,
    // so separation is what keeps the two captures apart.
    private const float LockedIslandY = -30000f;
    private const string MapLabel = "tactical map";

    private static HmdVisorBackend? _instance;

    private RectTransform? _hmdRect;
    private Canvas? _visorCanvas;
    private Camera? _visorCamera;
    private RenderTexture? _target;
    private VisorPanel? _panel;
    private float _nextRebind;
    private bool _active;
    private bool _loggedPlacement;
    private int _targetWidth;
    private int _targetHeight;

    private Canvas? _lockedCanvas;
    private Camera? _lockedCamera;
    private RenderTexture? _lockedTarget;
    private RectTransform? _lockedHolder;
    private VisorPanel? _lockedPanel;

    private Transform? _savedParent;
    private int _savedSiblingIndex;

    private readonly List<SpreadTarget> _spreadTargets = new();
    private float _nextSpreadRetry;

    public static bool IsVisorActive => _instance != null && _instance._active;

    /// <summary>
    /// The two quads one captured layer is drawn on — the shade behind, the
    /// symbology in front, both showing the same texture — and the canvas that
    /// carries them. See <see cref="ShadePanel"/> for why there are two.
    /// </summary>
    private sealed class VisorPanel
    {
        public Canvas Canvas = null!;
        public RectTransform Rect = null!;
        public RawImage Image = null!;
        public RawImage Shade = null!;
    }

    /// <summary>
    /// One element of the HMD layer that <see cref="SpreadPanels"/> can push
    /// outwards and <see cref="ApplyPanelLock"/> can move to the cockpit-fixed
    /// panel, with everything about it that does not change per frame.
    /// </summary>
    private sealed class SpreadTarget
    {
        public RectTransform Rect = null!;
        public string Label = "";
        /// <summary>Its untouched position, so the offset is set and never accumulated.</summary>
        public Vector3 BasePosition;
        /// <summary>Unit vector from the centre of view to the element, in canvas space.</summary>
        public Vector2 Direction;
        /// <summary>How far it can still travel along that vector before a corner leaves the capture.</summary>
        public float MaxDistance;
        /// <summary>Where in the HMD subtree it belongs, so the lock can be undone.</summary>
        public Transform SavedParent = null!;
        public int SavedSiblingIndex;
    }

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
        if (self == null || !self._active || self._visorCanvas == null) return null;

        var canvasRect = (RectTransform)self._visorCanvas.transform;
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

        if (_hmdRect == null && Time.unscaledTime >= _nextRebind)
        {
            _nextRebind = Time.unscaledTime + RebindInterval;
            _hmdRect = FindHmdRect();
        }

        if (_hmdRect == null)
        {
            if (_active) Teardown();
            return;
        }

        if (!_active) Setup();
        Maintain();
    }

    private static RectTransform? FindHmdRect()
    {
        // The component's own rect, never a parent canvas: the HMD is a subtree
        // of HUDCanvas, and walking up grabs the entire flight HUD (measured —
        // the first version of this backend did exactly that).
        var hmd = SceneSingleton<HeadMountedDisplay>.i;
        return hmd != null ? hmd.GetComponent<RectTransform>() : null;
    }

    private void Setup()
    {
        // The reference space HUDCanvas gave the subtree: 1920x1080 regardless
        // of the HMD size settings, which size the subtree's own rect within it.
        _targetWidth = 1920;
        _targetHeight = 1080;

        _target = CreateCaptureTarget("NOVR HMD Visor Capture");
        _visorCamera = CreateIslandCamera("NOVR HMD Visor Camera", IslandY, _target, -99f);
        _visorCanvas = CreateIslandCanvas("NOVR HMD Visor Canvas", _visorCamera);

        _savedParent = _hmdRect!.parent;
        _savedSiblingIndex = _hmdRect.GetSiblingIndex();
        _hmdRect.SetParent(_visorCanvas.transform, false);

        EnsurePanel();

        _active = true;
        Debug.Log($"[NOVR] HMD visor active: the game's helmet display on a head-locked panel, " +
                  $"{_targetWidth}x{_targetHeight} pixel-true capture.");
    }

    private void Teardown()
    {
        _active = false;

        // Put the two spread elements back before the subtree goes home, or the
        // offsets ride along into the flat game's own HUD — and take them off
        // the cockpit panel first, for the same reason.
        foreach (var target in _spreadTargets)
        {
            if (target.Rect != null) target.Rect.localPosition = target.BasePosition;
        }
        ReleaseLockedPanels();
        _spreadTargets.Clear();

        if (_hmdRect != null && _savedParent != null)
        {
            _hmdRect.SetParent(_savedParent, false);
            _hmdRect.SetSiblingIndex(_savedSiblingIndex);
        }
        _hmdRect = null;
        _savedParent = null;

        if (_visorCanvas != null)
        {
            Destroy(_visorCanvas.gameObject);
            _visorCanvas = null;
        }

        DestroyPanel(ref _panel);

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

    /// <summary>
    /// A capture texture at the HMD's reference resolution. Both layers use the
    /// same size and the same canvas geometry, so an element keeps its exact
    /// position and apparent size whichever panel it ends up on — the lock
    /// changes what the panel is attached to and nothing else.
    /// </summary>
    private RenderTexture CreateCaptureTarget(string name)
    {
        var target = new RenderTexture(_targetWidth, _targetHeight, 0, RenderTextureFormat.ARGB32)
        {
            name = name,
            filterMode = FilterMode.Bilinear,
            useMipMap = false,
            autoGenerateMips = false,
        };
        target.Create();
        return target;
    }

    private Camera CreateIslandCamera(string name, float islandY, RenderTexture target, float depth)
    {
        var go = new GameObject(name);
        go.transform.position = new Vector3(0f, islandY, 0f);
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
        camera.targetTexture = target;
        camera.depth = depth;
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
        return camera;
    }

    /// <summary>
    /// Our own canvas on an island camera; a game subtree moves into it and
    /// keeps its layout (worldPositionStays: false preserves local values, and
    /// the canvas rect matches the reference space it came from).
    /// </summary>
    private static Canvas CreateIslandCanvas(string name, Camera camera)
    {
        var canvasGo = new GameObject(name);
        var canvas = canvasGo.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceCamera;
        canvas.worldCamera = camera;
        canvas.planeDistance = 1f;
        return canvas;
    }

    private void EnsurePanel()
    {
        if (_panel != null) return;

        var hudCamera = APIBus.CockpitHudCamera;
        if (hudCamera == null || _target == null) return;

        // Head-locked: the visor is glued to the helmet, so its parent is the
        // pose-driven UI camera. This is the placement the airframe HUD panel
        // deliberately does not use — the two panels are the two halves of the
        // original game's split.
        _panel = CreatePanel("NOVR HMD Visor Panel", hudCamera.transform, _target, 3000);
    }

    private void EnsureLockedPanel()
    {
        if (_lockedPanel != null) return;

        var hudCamera = APIBus.CockpitHudCamera;
        if (hudCamera == null || _lockedTarget == null) return;

        // Cockpit-fixed, and for exactly the reason the flight HUD panel is:
        // this behaviour sits at identity under the NOVR root, whose local
        // space already *is* the airframe's frame, so a panel parked at a
        // constant local position under it holds still when the pilot looks
        // around and rolls with the aircraft. No anchoring code, nothing to go
        // stale. Drawn under the visor's queues so helmet symbology stays in
        // front of a cockpit display, which is where it physically is.
        _lockedPanel = CreatePanel("NOVR HMD Cockpit Panel", transform, _lockedTarget, 2998);
    }

    private VisorPanel CreatePanel(string name, Transform parent, RenderTexture texture, int baseQueue)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);

        var canvas = go.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        canvas.worldCamera = APIBus.CockpitHudCamera;

        var rect = (RectTransform)canvas.transform;

        // Two quads, same texture, drawn back to front: shade, then symbology.
        // See ShadePanel() for why these panels are the ones that get this.
        var shade = CreateQuad(rect, "Panel Shade", texture, ShadeMaterial(), baseQueue);
        var image = CreateQuad(rect, "Panel Texture", texture,
            FlightHudCaptureBackend.CreatePanelMaterial(), baseQueue + 1);

        LayerHelper.SetLayerRecursive(go.transform, LayerHelper.GetVrUiLayer());

        return new VisorPanel { Canvas = canvas, Rect = rect, Image = image, Shade = shade };
    }

    private void DestroyPanel(ref VisorPanel? panel)
    {
        if (panel == null) return;

        // The materials are `new Material(...)` instances, and destroying the
        // GameObject does not take them with it.
        if (panel.Image != null && panel.Image.material != null) Destroy(panel.Image.material);
        if (panel.Shade != null && panel.Shade.material != null) Destroy(panel.Shade.material);
        if (panel.Canvas != null) Destroy(panel.Canvas.gameObject);
        panel = null;
    }

    private RawImage CreateQuad(RectTransform parent, string name, Texture texture, Material material, int renderQueue)
    {
        var imageGo = new GameObject(name);
        imageGo.transform.SetParent(parent, false);

        var image = imageGo.AddComponent<RawImage>();
        image.texture = texture;
        image.raycastTarget = false;
        image.material = material;
        // Hierarchy order is not enough: a WorldSpace canvas sorts by material
        // render queue (the durable finding behind the masked-menu-text bug),
        // and these two quads are coincident. Say the order explicitly.
        image.material.renderQueue = renderQueue;

        var imageRect = (RectTransform)imageGo.transform;
        imageRect.anchorMin = Vector2.zero;
        imageRect.anchorMax = Vector2.one;
        imageRect.offsetMin = Vector2.zero;
        imageRect.offsetMax = Vector2.zero;

        return image;
    }

    /// <summary>
    /// The shade quad's material: the stock UI one, untouched. All the work is
    /// done by the graphic's colour, which <see cref="ShadePanel"/> sets to
    /// black with the shading strength as its alpha.
    ///
    /// <c>UI/Default</c> computes <c>src = tex * vertexColour</c> and blends
    /// <c>SrcAlpha, OneMinusSrcAlpha</c>. With a black vertex colour that is
    /// <c>dst = dst * (1 - texAlpha * strength)</c> — exactly alpha-compositing
    /// black over the view, which is what the game's two backing sprites are.
    /// No custom shader, and nothing to ship.
    /// </summary>
    private static Material ShadeMaterial() => new(Canvas.GetDefaultCanvasMaterial());

    private void Maintain()
    {
        // The subtree is the game's object; a scene event or an aircraft change
        // can rebuild things. If it left our canvas, stand down and let the
        // rebind find the new one.
        if (_hmdRect == null || _visorCanvas == null ||
            _hmdRect.parent != _visorCanvas.transform)
        {
            Teardown();
            return;
        }

        EnsurePanel();
        if (_panel == null) return;

        var distance = CapturedFlightHud.DistanceMeters;
        var fovDegrees = Mathf.Clamp(CapturedHmd.VisorFieldOfView?.Value ?? 70f, 30f, 110f);
        var widthMeters = 2f * distance * Mathf.Tan(fovDegrees * 0.5f * Mathf.Deg2Rad);

        EnsureSpreadTargets();
        ApplyPanelLock();
        SpreadPanels(fovDegrees);

        PlacePanel(_panel, distance, widthMeters);
        if (_lockedPanel != null) PlacePanel(_lockedPanel, distance, widthMeters);

        if (_loggedPlacement) return;
        _loggedPlacement = true;
        Debug.Log($"[NOVR] HMD visor panel: {widthMeters:F1} m wide at {distance:F1} m " +
                  $"({fovDegrees:F0}deg horizontal), head-locked.");
    }

    /// <summary>
    /// Size and park one panel. Both are the same size at the same distance and
    /// differ only in what they hang off — the visor's parent is the pose-driven
    /// camera, the cockpit panel's is the airframe frame.
    /// </summary>
    private void PlacePanel(VisorPanel panel, float distance, float widthMeters)
    {
        if (panel.Image != null && panel.Image.material != null)
        {
            var brightness = Mathf.Clamp(CapturedFlightHud.Brightness?.Value ?? 2f, 0.25f, 8f);
            panel.Image.material.SetColor("_Color", new Color(brightness, brightness, brightness, 1f));
        }

        ShadePanel(panel);

        var aspect = (float)_targetWidth / _targetHeight;
        panel.Rect.sizeDelta = new Vector2(PanelCanvasReferenceWidth, PanelCanvasReferenceWidth / aspect);
        var scale = widthMeters / PanelCanvasReferenceWidth;
        panel.Rect.localScale = new Vector3(scale, scale, scale);
        panel.Rect.localPosition = new Vector3(0f, 0f, distance);
        panel.Rect.localRotation = Quaternion.identity;
    }

    /// <summary>
    /// Move the tactical map and the weapon/countermeasure readout off the
    /// head-locked visor and onto a panel fixed in the cockpit — or put them
    /// back, when the setting is off.
    ///
    /// They are captured separately rather than masked out of the visor
    /// texture, because a panel is one quad showing one texture: to draw two
    /// parts of the helmet layer in two different frames of reference, the two
    /// parts have to be rendered apart. The second capture is the same size,
    /// the same canvas geometry and the same field of view as the first, and
    /// the holder below copies the HMD rect frame by frame — so an element
    /// crossing over keeps its exact position and apparent size, and only what
    /// the panel is attached to changes.
    /// </summary>
    private void ApplyPanelLock()
    {
        if (!(CapturedHmd.PanelLock?.Value ?? true))
        {
            ReleaseLockedPanels();
            return;
        }

        EnsureLockedCapture();
        EnsureLockedPanel();
        if (_lockedHolder == null) return;

        MirrorHmdRect();

        foreach (var target in _spreadTargets)
        {
            if (target.Rect == null || target.Rect.parent == _lockedHolder) continue;
            target.Rect.SetParent(_lockedHolder, false);
        }
    }

    private void EnsureLockedCapture()
    {
        if (_lockedHolder != null) return;

        _lockedTarget = CreateCaptureTarget("NOVR HMD Cockpit Capture");
        _lockedCamera = CreateIslandCamera("NOVR HMD Cockpit Camera", LockedIslandY, _lockedTarget, -98f);
        _lockedCanvas = CreateIslandCanvas("NOVR HMD Cockpit Canvas", _lockedCamera);

        var holderGo = new GameObject("HMD Cockpit Elements");
        var holder = holderGo.AddComponent<RectTransform>();
        holder.SetParent(_lockedCanvas.transform, false);
        _lockedHolder = holder;

        Debug.Log("[NOVR] HMD cockpit panel active: the tactical map and the weapon readout " +
                  "fixed in the airframe instead of riding the head-locked visor.");
    }

    /// <summary>
    /// Keep the holder the same rect the HMD subtree is, so a reparented child
    /// lays out identically. It matters because the rect is not a constant:
    /// <c>HeadMountedDisplay.RefreshSettings</c> resizes it from the player's
    /// `hmdWidth`/`hmdHeight` whenever options are applied, and a child anchored
    /// to a corner would otherwise move when the holder disagreed.
    /// </summary>
    private void MirrorHmdRect()
    {
        if (_lockedHolder == null || _hmdRect == null) return;

        _lockedHolder.anchorMin = _hmdRect.anchorMin;
        _lockedHolder.anchorMax = _hmdRect.anchorMax;
        _lockedHolder.pivot = _hmdRect.pivot;
        _lockedHolder.sizeDelta = _hmdRect.sizeDelta;
        _lockedHolder.anchoredPosition3D = _hmdRect.anchoredPosition3D;
        _lockedHolder.localRotation = _hmdRect.localRotation;
        _lockedHolder.localScale = _hmdRect.localScale;
    }

    /// <summary>
    /// Put the two elements back in the HMD subtree and tear the second capture
    /// down. Idempotent: it is the "off" branch of the setting, the first step
    /// of a re-resolve, and part of teardown.
    /// </summary>
    private void ReleaseLockedPanels()
    {
        foreach (var target in _spreadTargets)
        {
            if (target.Rect == null || target.SavedParent == null) continue;
            if (target.Rect.parent == target.SavedParent) continue;
            target.Rect.SetParent(target.SavedParent, false);
            target.Rect.SetSiblingIndex(target.SavedSiblingIndex);
        }

        DestroyPanel(ref _lockedPanel);

        if (_lockedCanvas != null)
        {
            Destroy(_lockedCanvas.gameObject);
            _lockedCanvas = null;
        }
        _lockedHolder = null;

        if (_lockedCamera != null)
        {
            VrCameraManager.IgnoredCameras.Remove(_lockedCamera);
            Destroy(_lockedCamera.gameObject);
            _lockedCamera = null;
        }

        if (_lockedTarget != null)
        {
            _lockedTarget.Release();
            Destroy(_lockedTarget);
            _lockedTarget = null;
        }
    }

    /// <summary>
    /// Give back the flat game's dark backings behind the weapon readout, the
    /// tactical map and the HMD number boxes.
    ///
    /// Those backings are two sprites, <c>mapPanel</c> and <c>weaponsPanel</c>
    /// (plus the readout boxes), and they are authored as the exact opposite of
    /// the rest of the HUD: pure black RGB with a real alpha channel and a
    /// soft ~8 texel edge ramp, i.e. darkening masks, where the symbology
    /// textures are BC1 with no alpha at all and are meant to be added. The
    /// panels therefore write alpha into the capture texture and no colour, so
    /// an additive quad drops them entirely — which is why they were missing.
    ///
    /// A combiner cannot darken, and the airframe HUD panel is deliberately
    /// modelled as one. These two are not: they are displays glued to the
    /// helmet or bolted to the cockpit, not symbology projected through glass,
    /// so darkening is the faithful behaviour rather than a violation of it.
    /// Measured on the flat game, the backings pass 34-39% of what is behind
    /// them. Both panels get the same treatment because the split moved two of
    /// the backings and left the rest — where a backing ended up is not a
    /// reason to render it differently.
    ///
    /// The strength knob exists because the texture's alpha is *saturated*, not
    /// authored: every draw over the panel accumulates alpha, so a region the
    /// artist made 0.65 opaque arrives at 0.87-1.00. Scaling it back is what
    /// makes the result match the flat frame; it is also what keeps the
    /// difference between an icon quad and bare panel below anything visible.
    /// </summary>
    private void ShadePanel(VisorPanel panel)
    {
        if (panel.Shade == null) return;

        var authored = Mathf.Clamp01(CapturedHmd.PanelShading?.Value ?? 0.65f);

        // The setting is in the flat game's units — "pass 35% of the light" —
        // and this converts it to the units the blend actually runs in.
        //
        // The flat game's backing is a ScreenSpaceOverlay draw: it multiplies
        // gamma-encoded pixels. This quad is drawn by the pose-driven UI camera
        // into a linear target, so the same alpha multiplies linear light and
        // comes out far lighter after the transfer curve. Measured: at 0.65 the
        // panel passed 0.66-0.69 of the sky behind it where the flat game
        // passes 0.34-0.39 — predicted 0.68 by (1 - a)^(1/2.2), which is what
        // confirmed the space rather than some other loss.
        var strength = 1f - Mathf.Pow(1f - authored, 2.2f);

        panel.Shade.enabled = authored > 0f;
        panel.Shade.color = new Color(0f, 0f, 0f, strength);
    }

    /// <summary>
    /// Push the tactical map and the weapon/countermeasure readout further from
    /// the centre of view.
    ///
    /// The flat game puts them in screen corners, and a screen is much wider
    /// than the visor: mapped onto a 70 degree panel they end up crowding the
    /// middle of the pilot's view, right where the flight HUD is. This moves
    /// them back out along their own bearing from the centre, in degrees, which
    /// is the unit that means something here — the visor's pixels only exist
    /// because the game's arithmetic wanted them. It applies on whichever panel
    /// they are on: the crowding is a property of the mapping, not of what the
    /// panel is bolted to.
    ///
    /// Only these two. The four HMD widgets (speed, altitude, bearing, horizon)
    /// are deliberately left alone: <c>HeadMountedDisplay.RefreshSettings</c>
    /// rewrites their positions from `hmdSideDist` / `hmdSideAngle` /
    /// `hmdTopHeight` whenever the player applies options, so the game already
    /// owns that placement and fighting it would be a bug rather than a
    /// feature. The map and the readout are the two it does not touch.
    /// </summary>
    private void SpreadPanels(float fovDegrees)
    {
        var degrees = Mathf.Clamp(CapturedHmd.PanelSpread?.Value ?? 10f, 0f, 30f);
        var pixels = degrees * _targetWidth / Mathf.Max(1f, fovDegrees);

        foreach (var target in _spreadTargets)
        {
            if (target.Rect == null) continue;
            var distance = Mathf.Min(pixels, target.MaxDistance);
            var canvasDelta = (Vector3)(target.Direction * distance);
            // The HMD rect, the cockpit holder that mirrors it and both canvases
            // are unrotated UI rects at scale 1, so this is the identity in
            // practice on either panel. Going through the transforms anyway
            // costs nothing and survives someone scaling the subtree later.
            var localDelta = target.Rect.parent != null
                ? target.Rect.parent.InverseTransformVector(_visorCanvas!.transform.TransformVector(canvasDelta))
                : canvasDelta;
            target.Rect.localPosition = target.BasePosition + localDelta;
        }
    }

    private void EnsureSpreadTargets()
    {
        if (_spreadTargets.Count == 0 || _spreadTargets.Exists(t => t.Rect == null))
        {
            ResolveSpreadTargets();
            return;
        }

        // The map can arrive after the first resolve: `DynamicMap` is a scene
        // singleton like any other and need not exist when the HMD does. Retry
        // for that one alone rather than re-resolving, which would tear the
        // second capture down and build it again on every attempt.
        if (_spreadTargets.Exists(t => t.Label == MapLabel)) return;
        if (Time.unscaledTime < _nextSpreadRetry) return;
        _nextSpreadRetry = Time.unscaledTime + RebindInterval;
        AddMapSpreadTarget(_visorCanvas!.transform);
    }

    /// <summary>
    /// Find the weapon readout and the tactical map, by the components they
    /// contain or reference rather than by name or path: names are the game's
    /// to change, and a component is what the element actually is. Each is
    /// taken as a whole — the direct child of the HMD rect that owns it — so
    /// nothing inside is disturbed. The map needs its own route, for the reason
    /// <see cref="AddMapSpreadTarget"/> gives.
    /// </summary>
    private void ResolveSpreadTargets()
    {
        // Whatever was already found goes back to the game's own arrangement
        // before the scan. The base position has to be the untouched one, and a
        // rect that has been spread, or moved to the cockpit panel, would
        // otherwise be re-recorded at its offset or missed by the search.
        foreach (var target in _spreadTargets)
        {
            if (target.Rect != null) target.Rect.localPosition = target.BasePosition;
        }
        ReleaseLockedPanels();
        _spreadTargets.Clear();

        if (_hmdRect == null || _visorCanvas == null) return;

        var canvasTransform = _visorCanvas.transform;
        var corners = new Vector3[4];

        foreach (Transform child in _hmdRect)
        {
            if (child is not RectTransform rect) continue;
            if (child.GetComponentInChildren<WeaponStatus>(true) == null) continue;

            rect.GetWorldCorners(corners);
            AddSpreadTarget(rect, "weapon readout",
                canvasTransform.InverseTransformPoint(corners[0]),
                canvasTransform.InverseTransformPoint(corners[2]));
        }

        AddMapSpreadTarget(canvasTransform);
    }

    /// <summary>
    /// The tactical map, found through the game's own anchor rather than by
    /// searching the HMD subtree for a <c>DynamicMap</c>.
    ///
    /// Searching cannot work, because the map is not always there:
    /// <c>DynamicMap.Maximize</c> reparents it onto its own full-screen canvas
    /// and <c>Minimize</c> puts it back under <c>hudMapAnchor</c>, so a
    /// component sweep finds it only while it happens to be docked. Measured —
    /// a harness run whose map was maximized moved the weapon readout and
    /// silently left the map behind, which is also why the spread has never
    /// actually moved it.
    ///
    /// What is stable is the anchor. The game keeps it inside the HMD subtree
    /// and docks the map into it, so taking the HMD child that *contains* the
    /// anchor carries the map wherever the game next puts it, in either state
    /// and with no per-frame work.
    ///
    /// Its extent comes from the game's own numbers for the same reason:
    /// <c>mapScaleMinimized</c> plus the 20 px <c>Minimize</c> adds for the
    /// background, centred on the anchor, which is exactly where <c>Minimize</c>
    /// parks it (<c>localPosition = Vector3.zero</c>). Measuring the rect
    /// instead would measure an empty container whenever the map was maximized.
    /// </summary>
    private void AddMapSpreadTarget(Transform canvasTransform)
    {
        var map = SceneSingleton<DynamicMap>.i;
        var anchor = map != null ? map.hudMapAnchor : null;
        if (anchor == null || _hmdRect == null) return;

        Transform? container = anchor;
        while (container != null && container.parent != _hmdRect) container = container.parent;
        if (container is not RectTransform rect) return;

        var centre = canvasTransform.InverseTransformPoint(anchor.position);
        var half = (map!.mapScaleMinimized + 20f) * 0.5f;
        AddSpreadTarget(rect, MapLabel,
            new Vector3(centre.x - half, centre.y - half, centre.z),
            new Vector3(centre.x + half, centre.y + half, centre.z));
    }

    /// <summary>
    /// Record one element, with the corner of its extent in canvas space that
    /// says which way "outwards" is and how far it can go.
    /// </summary>
    private void AddSpreadTarget(RectTransform rect, string label, Vector3 min, Vector3 max)
    {
        var centre = new Vector2((min.x + max.x) * 0.5f, (min.y + max.y) * 0.5f);
        if (centre.sqrMagnitude < 1f) return;   // dead centre: no outward direction to take

        var direction = centre.normalized;
        var maxDistance = Mathf.Max(0f, Mathf.Min(
            Allowance(direction.x, min.x, max.x, _targetWidth * 0.5f),
            Allowance(direction.y, min.y, max.y, _targetHeight * 0.5f)));

        _spreadTargets.Add(new SpreadTarget
        {
            Rect = rect,
            Label = label,
            BasePosition = rect.localPosition,
            Direction = direction,
            MaxDistance = maxDistance,
            SavedParent = _hmdRect!,
            SavedSiblingIndex = rect.GetSiblingIndex(),
        });

        Debug.Log($"[NOVR] HMD spread: {label} can move {maxDistance:F0} px outwards " +
                  $"before it leaves the capture.");
    }

    /// <summary>
    /// How far a rect spanning <paramref name="lo"/>..<paramref name="hi"/> on
    /// one axis can travel at rate <paramref name="d"/> before its leading edge
    /// passes +/-<paramref name="half"/>. An axis it is not moving along cannot
    /// be the binding one.
    /// </summary>
    private static float Allowance(float d, float lo, float hi, float half)
    {
        if (Mathf.Abs(d) < 1e-4f) return float.MaxValue;
        return d > 0f ? (half - hi) / d : (lo + half) / -d;
    }
}
