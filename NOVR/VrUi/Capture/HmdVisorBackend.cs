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
/// </summary>
public class HmdVisorBackend : NOVRBehaviour
{
    private const float PanelCanvasReferenceWidth = 1000f;
    private const float RebindInterval = 0.5f;
    private const float IslandY = -25000f;

    private static HmdVisorBackend? _instance;

    private RectTransform? _hmdRect;
    private Canvas? _visorCanvas;
    private Camera? _visorCamera;
    private RenderTexture? _target;
    private Canvas? _panelCanvas;
    private RawImage? _panelImage;
    private RawImage? _shadeImage;
    private RectTransform? _panelRect;
    private float _nextRebind;
    private bool _active;
    private bool _loggedPlacement;
    private int _targetWidth;
    private int _targetHeight;

    private Transform? _savedParent;
    private int _savedSiblingIndex;

    private readonly List<SpreadTarget> _spreadTargets = new();
    private bool _loggedSpread;
    private bool _rollUnshaded;
    private Material? _colourOnly;
    /// <summary>What the roll indicator's graphics were drawn with, so the game gets them back.</summary>
    private readonly List<(Graphic Graphic, Material? Material)> _rollMaterials = new();

    public static bool IsVisorActive => _instance != null && _instance._active;

    /// <summary>
    /// One element of the HMD layer that <see cref="SpreadPanels"/> can push
    /// outwards, with everything about it that does not change per frame.
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

        // Our own canvas on the island camera; the game's subtree moves into
        // it and keeps its layout (worldPositionStays: false preserves local
        // values, and the canvas rect matches the reference space it came from).
        var canvasGo = new GameObject("NOVR HMD Visor Canvas");
        var canvas = canvasGo.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceCamera;
        canvas.worldCamera = camera;
        canvas.planeDistance = 1f;
        _visorCanvas = canvas;

        _savedParent = _hmdRect!.parent;
        _savedSiblingIndex = _hmdRect.GetSiblingIndex();
        _hmdRect.SetParent(canvas.transform, false);

        EnsurePanel();

        _active = true;
        Debug.Log($"[NOVR] HMD visor active: the game's helmet display on a head-locked panel, " +
                  $"{_targetWidth}x{_targetHeight} pixel-true capture.");
    }

    private void Teardown()
    {
        _active = false;

        // Put the two spread elements back before the subtree goes home, or the
        // offsets ride along into the flat game's own HUD.
        foreach (var target in _spreadTargets)
        {
            if (target.Rect != null) target.Rect.localPosition = target.BasePosition;
        }
        _spreadTargets.Clear();
        _loggedSpread = false;

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

        if (_panelCanvas != null)
        {
            if (_panelImage != null && _panelImage.material != null) Destroy(_panelImage.material);
            foreach (var (graphic, original) in _rollMaterials)
            {
                if (graphic != null) graphic.material = original;
            }
            _rollMaterials.Clear();
            _rollUnshaded = false;
            if (_colourOnly != null) Destroy(_colourOnly);
            _colourOnly = null;
            if (_shadeImage != null && _shadeImage.material != null) Destroy(_shadeImage.material);
            Destroy(_panelCanvas.gameObject);
            _panelCanvas = null;
            _panelImage = null;
            _shadeImage = null;
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

        // Two quads, same texture, drawn back to front: shade, then symbology.
        // See ShadeVisor() for why the visor is the one panel that gets this.
        var shade = CreateQuad(rect, "Visor Shade", ShadeMaterial(), 3000);
        var image = CreateQuad(rect, "Visor Texture", FlightHudCaptureBackend.CreatePanelMaterial(), 3001);

        LayerHelper.SetLayerRecursive(go.transform, LayerHelper.GetVrUiLayer());

        _panelCanvas = canvas;
        _panelImage = image;
        _shadeImage = shade;
        _panelRect = rect;
    }

    private RawImage CreateQuad(RectTransform parent, string name, Material material, int renderQueue)
    {
        var imageGo = new GameObject(name);
        imageGo.transform.SetParent(parent, false);

        var image = imageGo.AddComponent<RawImage>();
        image.texture = _target;
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
    /// done by the graphic's colour, which <see cref="ShadeVisor"/> sets to
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
        if (_panelRect == null) return;

        var distance = Mathf.Clamp(CapturedFlightHud.Distance?.Value ?? 25f, 2f, 200f);
        var fovDegrees = Mathf.Clamp(CapturedHmd.VisorFieldOfView?.Value ?? 70f, 30f, 110f);
        var widthMeters = 2f * distance * Mathf.Tan(fovDegrees * 0.5f * Mathf.Deg2Rad);

        if (_panelImage != null && _panelImage.material != null)
        {
            var brightness = Mathf.Clamp(CapturedFlightHud.Brightness?.Value ?? 2f, 0.25f, 8f);
            _panelImage.material.SetColor("_Color", new Color(brightness, brightness, brightness, 1f));
        }

        ShadeVisor();
        UnshadeRollIndicator();
        SpreadPanels(fovDegrees);

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
    /// modelled as one. The visor is not: these elements are displays glued to
    /// the helmet, not symbology projected through glass, so darkening is the
    /// faithful behaviour rather than a violation of it. Measured on the flat
    /// game, the backings pass 34-39% of what is behind them.
    ///
    /// The strength knob exists because the texture's alpha is *saturated*, not
    /// authored: every draw over the panel accumulates alpha, so a region the
    /// artist made 0.65 opaque arrives at 0.87-1.00. Scaling it back is what
    /// makes the result match the flat frame; it is also what keeps the
    /// difference between an icon quad and bare panel below anything visible.
    /// </summary>
    private void ShadeVisor()
    {
        if (_shadeImage == null) return;

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

        _shadeImage.enabled = authored > 0f;
        _shadeImage.color = new Color(0f, 0f, 0f, strength);
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
    /// because the game's arithmetic wanted them.
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
        if (_spreadTargets.Count == 0 || _spreadTargets.Exists(t => t.Rect == null))
        {
            ResolveSpreadTargets();
        }

        var degrees = Mathf.Clamp(CapturedHmd.PanelSpread?.Value ?? 10f, 0f, 30f);
        var pixels = degrees * _targetWidth / Mathf.Max(1f, fovDegrees);

        foreach (var target in _spreadTargets)
        {
            if (target.Rect == null) continue;
            var distance = Mathf.Min(pixels, target.MaxDistance);
            var canvasDelta = (Vector3)(target.Direction * distance);
            // The HMD rect and the canvas are both unrotated UI rects, so this
            // is the identity in practice. Going through the transforms anyway
            // costs nothing and survives someone scaling the subtree later.
            var localDelta = target.Rect.parent != null
                ? target.Rect.parent.InverseTransformVector(_visorCanvas!.transform.TransformVector(canvasDelta))
                : canvasDelta;
            target.Rect.localPosition = target.BasePosition + localDelta;
        }
    }

    /// <summary>
    /// A UI material that writes colour and leaves the alpha channel alone.
    /// <c>_ColorMask</c> is a real property on <c>UI/Default</c>, so this needs no
    /// shader of its own: 14 is R|G|B, the same mask minus A.
    /// </summary>
    private Material? ColourOnlyMaterial()
    {
        if (_colourOnly != null) return _colourOnly;

        var shader = Shader.Find("UI/Default");
        if (shader == null)
        {
            Debug.LogWarning("[NOVR] HMD visor: no 'UI/Default' shader, so the roll indicator " +
                             "stays inside the darkening mask.");
            return null;
        }

        _colourOnly = new Material(shader) { name = "NOVR Visor Colour Only" };
        _colourOnly.SetFloat("_ColorMask", 14f);
        return _colourOnly;
    }

    /// <summary>
    /// Take the roll indicator out of the darkening mask.
    ///
    /// <para>The shade quad is masked by the capture's own alpha channel, which is
    /// exactly right for the two large backings it was built for — they are black
    /// sprites whose alpha <i>is</i> the mask — and it sweeps up anything else
    /// that writes alpha. The HMD's readouts do. Measured off the visor capture:
    /// the speed and altitude boxes write only their border, and the roll box
    /// (<c>horizon</c>, the one showing bank in degrees) writes a filled
    /// rectangle. So that one comes out as a dark slab in the middle of the view
    /// where the others come out as a thin outline.</para>
    ///
    /// <para><b>And there is no backing there at all.</b> Asked what it contains,
    /// the widget is three symbology Images and nothing else: <c>vector</c>
    /// (velocityVector, green), <c>horizon</c> (exclusionCircle, green) and
    /// <c>sky</c> (exclusionCircle, blue at 0.8 alpha). The dark slab is the
    /// <c>sky</c> disc — an instrument, drawn semi-transparent — being darkened by
    /// its own alpha. Two earlier guesses died here: a black tint (the boxes are
    /// tinted white and it is the *sprite* that is black, so the colour says
    /// nothing) and "clear the alpha of the backing" (there is no backing, and
    /// clearing alpha on an Image removes its colour too, which would delete the
    /// attitude indicator rather than un-darken it).</para>
    ///
    /// <para><b>So stop it writing alpha without stopping it drawing.</b>
    /// <c>UI/Default</c> exposes <c>_ColorMask</c>, so a material that writes RGB
    /// and not A lets the widget composite into the capture's colour exactly as
    /// before while contributing nothing to the channel the shade is masked by.
    /// The instrument is untouched and the slab is gone, with no shader shipped
    /// and nothing else on the visor affected.</para>
    /// </summary>
    private void UnshadeRollIndicator()
    {
        if (_rollUnshaded) return;

        var hmd = SceneSingleton<HeadMountedDisplay>.i;
        if (hmd == null) return;

        var horizon = HarmonyLib.AccessTools
            .Field(typeof(HeadMountedDisplay), "horizon")?.GetValue(hmd) as Component;
        if (horizon == null) return;

        _rollUnshaded = true;

        var material = ColourOnlyMaterial();
        if (material == null) return;

        var masked = 0;
        foreach (var graphic in horizon.transform.GetComponentsInChildren<Graphic>(true))
        {
            var image = graphic as Image;
            var sprite = image != null && image.sprite != null ? image.sprite.name : "<none>";

            Debug.Log($"[NOVR] HMD visor roll indicator: '{graphic.name}' " +
                      $"{graphic.GetType().Name} colour={graphic.color} sprite='{sprite}' " +
                      "— drawing colour only, no alpha.");

            _rollMaterials.Add((graphic, graphic.material));
            graphic.material = material;
            masked++;
        }

        Debug.Log($"[NOVR] HMD visor: roll indicator taken out of the darkening mask — " +
                  $"{masked} graphic(s) now write colour without alpha, so the shade quad " +
                  "has nothing of theirs to darken.");
    }

    /// <summary>
    /// Find the map and the weapon readout among the HMD subtree's own
    /// children, by the components they contain rather than by name or path:
    /// names are the game's to change, and a component is what the element
    /// actually is. Each is moved as a whole — the direct child of the HMD rect
    /// that owns it — so nothing inside is disturbed.
    /// </summary>
    private void ResolveSpreadTargets()
    {
        _spreadTargets.Clear();
        if (_hmdRect == null || _visorCanvas == null) return;

        var canvasTransform = _visorCanvas.transform;
        var halfWidth = _targetWidth * 0.5f;
        var halfHeight = _targetHeight * 0.5f;
        var corners = new Vector3[4];

        foreach (Transform child in _hmdRect)
        {
            if (child is not RectTransform rect) continue;

            var label = child.GetComponentInChildren<DynamicMap>(true) != null ? "tactical map"
                : child.GetComponentInChildren<WeaponStatus>(true) != null ? "weapon readout"
                : null;
            if (label == null) continue;

            rect.GetWorldCorners(corners);
            var min = canvasTransform.InverseTransformPoint(corners[0]);
            var max = canvasTransform.InverseTransformPoint(corners[2]);
            var centre = new Vector2((min.x + max.x) * 0.5f, (min.y + max.y) * 0.5f);
            if (centre.sqrMagnitude < 1f) continue;   // dead centre: no outward direction to take

            var direction = centre.normalized;
            var maxDistance = Mathf.Max(0f, Mathf.Min(
                Allowance(direction.x, min.x, max.x, halfWidth),
                Allowance(direction.y, min.y, max.y, halfHeight)));

            _spreadTargets.Add(new SpreadTarget
            {
                Rect = rect,
                Label = label,
                BasePosition = rect.localPosition,
                Direction = direction,
                MaxDistance = maxDistance,
            });
        }

        if (_loggedSpread || _spreadTargets.Count == 0) return;
        _loggedSpread = true;
        foreach (var target in _spreadTargets)
        {
            Debug.Log($"[NOVR] HMD visor spread: {target.Label} can move " +
                      $"{target.MaxDistance:F0} px outwards before it leaves the capture.");
        }
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
