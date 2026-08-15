using System.Collections.Generic;
using NOVR.VrUi;
using UnityEngine;

namespace NOVR.VrMap;

/// <summary>
/// The 3D world map: the whole theatre as a solid model hanging below you, and
/// the shortcut that shows and hides it.
///
/// <para><b>Why a model and not a camera pointed at the world.</b> The obvious
/// way to get a map like this is to fly a camera to 40 km and look down, which
/// needs no new geometry at all. It also produces a flat picture: at that range
/// the two eyes see the same image, so a headset shows you a photograph. The
/// whole value of doing this in VR is the diorama — depth you get by having the
/// thing be small and near. So the world is shrunk instead of the viewer being
/// moved.</para>
///
/// <para><b>Why it is drawn by the UI camera.</b> The model lives on the VR UI
/// layer, which <see cref="NOUIManager.CockpitHudCamera"/> draws as a URP
/// overlay with the depth buffer cleared. That is what lets a landscape tens of
/// metres across sit in a cockpit you are strapped into without the canopy rails
/// cutting through it. It also puts the model behind the HUD panels rather than
/// over them, since the model is opaque geometry and they are transparent.</para>
///
/// <para><b>Why it does not follow your head.</b> The model is placed against
/// the airframe mount, not the head: a model anchored to the head moves when you
/// lean, and a world that moves when you lean is the single most reliable way to
/// make someone sick. It is world-aligned too — north stays north as you turn,
/// so the model behaves like an object you are flying over rather than a thing
/// strapped to the aircraft.</para>
/// </summary>
public class VrWorldMap : NOVRBehaviour
{
    /// <summary>
    /// What the cockpit is drawn on. Both go while the map is up: you are
    /// supposed to be over the landscape, not looking at it past a canopy rail.
    /// </summary>
    private const int CockpitLayers = (1 << (int)LayerHelper.Layers.Cockpit)
                                      | (1 << (int)LayerHelper.Layers.CockpitAndExternal);

    private WorldMapModel? _model;
    private bool _reported;
    private GameObject? _marker;
    private readonly Dictionary<Camera, int> _maskedCameras = new();
    private readonly List<HiddenPanel> _hiddenPanels = new();

    /// <summary>
    /// A panel we took out of view, and exactly what it takes to put it back.
    ///
    /// <para>Two mechanisms, because the two panels need different ones. A
    /// <c>Canvas</c> is switched off where there is one: the game re-activates
    /// the map's GameObject on its own (<c>DynamicMap.EnableCanvas</c>), so
    /// deactivating it is a fight we lose every frame, while a disabled Canvas
    /// component survives it. Where there is no Canvas, the GameObject.</para>
    /// </summary>
    private readonly struct HiddenPanel
    {
        public HiddenPanel(GameObject go, Canvas? canvas)
        {
            Go = go;
            Canvas = canvas;
            WasActive = go.activeSelf;
            WasEnabled = canvas != null && canvas.enabled;
        }

        public readonly GameObject Go;
        public readonly Canvas? Canvas;
        public readonly bool WasActive;
        public readonly bool WasEnabled;

        public void Hide()
        {
            if (Canvas != null)
            {
                if (Canvas.enabled) Canvas.enabled = false;
            }
            else if (Go != null && Go.activeSelf)
            {
                Go.SetActive(false);
            }
        }

        public void Restore()
        {
            if (Canvas != null) Canvas.enabled = WasEnabled;
            else if (Go != null) Go.SetActive(WasActive);
        }
    }

    protected override void OnDisable()
    {
        base.OnDisable();
        ShowCockpit();
        ShowHelmetPanels();
    }

    private void OnDestroy()
    {
        ShowCockpit();
        ShowHelmetPanels();
        if (_marker != null) Destroy(_marker);
        _marker = null;
        _model?.Destroy();
        _model = null;
    }

    private void Update()
    {
        if (VrMapConfig.ToggleShortcut != null && Input.GetKeyDown(VrMapConfig.ToggleShortcut.Value))
        {
            VrMapConfig.Open.Value = !VrMapConfig.Open.Value;
        }

        if (VrMapConfig.Enabled == null || !VrMapConfig.Enabled.Value || !VrMapConfig.Open.Value)
        {
            Hide();
            return;
        }

        var model = EnsureModel();
        if (model == null) return;

        Place(model);
        model.Root.SetActive(true);
        HideCockpit();
        HideHelmetPanels();
        ReportOnce(model);
    }

    /// <summary>
    /// Fade the tactical map and the weapon/countermeasure readout off the
    /// helmet while the model is up. Both sit in front of the same view the
    /// model fills, and a small flat map of the ground is the one thing a big
    /// solid one makes redundant.
    ///
    /// <para>Found by what they are, not where they sit: the weapon readout is
    /// the child of the helmet rect that owns a <c>WeaponStatus</c>, and the map
    /// is the <c>DynamicMap</c> scene singleton, asked for directly because it
    /// is not under the helmet at all.</para>
    ///
    /// <para>Deactivated rather than faded. A CanvasGroup at alpha 0 took the
    /// weapon readout out and left the tactical map drawing, so something under
    /// the map does not inherit the group. Deactivating is also the state the
    /// game itself uses for the map — <c>DynamicMap.EnableCanvas(false)</c> does
    /// exactly this — so it is a supported thing to do to it rather than a trick
    /// that happens to work.</para>
    /// </summary>
    private void HideHelmetPanels()
    {
        if (VrMapConfig.HideHelmetPanels == null || !VrMapConfig.HideHelmetPanels.Value)
        {
            ShowHelmetPanels();
            return;
        }

        if (_hiddenPanels.Count < 2)
        {
            var hmd = SceneSingleton<HeadMountedDisplay>.i;
            if (hmd != null)
            {
                foreach (Transform child in hmd.transform)
                {
                    if (child.GetComponentInChildren<WeaponStatus>(true) == null) continue;
                    TakePanel(child.gameObject, "weapon readout");
                }
            }

            // The tactical map is not reached through the helmet rect. Measured:
            // the helmet's children are Speed, Altitude, Bearing,
            // ArtificialHorizon, TopRightPanel and LowerLeftPanel, and none of
            // them holds a DynamicMap — the map is its own scene singleton,
            // placed at an anchor in the helmet rather than living under it.
            var map = SceneSingleton<global::DynamicMap>.i;
            if (map != null) TakePanel(map.gameObject, "tactical map");
        }

        // Every frame, not once. The game turns the map's GameObject back on by
        // itself, so a single hide is undone before it is ever seen.
        foreach (var panel in _hiddenPanels) panel.Hide();
    }

    private void TakePanel(GameObject panel, string what)
    {
        foreach (var known in _hiddenPanels)
        {
            if (known.Go == panel) return;
        }

        var canvas = panel.GetComponent<Canvas>();
        _hiddenPanels.Add(new HiddenPanel(panel, canvas));
        Debug.Log(
            $"[NOVR] World map: hiding the {what} ('{panel.name}') by " +
            (canvas != null ? "disabling its Canvas" : "deactivating it") + ".");
    }

    private void ShowHelmetPanels()
    {
        if (_hiddenPanels.Count == 0) return;
        foreach (var panel in _hiddenPanels) panel.Restore();
        _hiddenPanels.Clear();
    }

    /// <summary>
    /// One line, the first frame the model is up, saying where it actually is
    /// and what frame it went into. "The map opens and there is nothing in it"
    /// has too many possible causes to diagnose from a screenshot: the model
    /// could be somewhere else, on the wrong layer, culled, or drawn and
    /// invisible. These are the numbers that separate those, and they are what
    /// caught the room the first time.
    /// </summary>
    private void ReportOnce(WorldMapModel model)
    {
        if (_reported) return;
        _reported = true;

        var root = model.Root.transform;
        var first = model.Root.GetComponentInChildren<MeshRenderer>();
        var camera = NOUIManager.I != null ? NOUIManager.I.CockpitHudCamera : null;
        var room = NOUIManager.I != null ? NOUIManager.I.transform : null;
        var mount = Mount();

        Debug.Log(
            $"[NOVR] World map placed: root local={root.localPosition} scale={root.localScale.x:E3} " +
            $"layer={model.Root.layer} active={model.Root.activeInHierarchy} " +
            $"datum={(global::Datum.originPosition)}");
        Debug.Log(
            "[NOVR] World map frames: " +
            $"room={(room == null ? "<none>" : $"{room.position} rot={room.rotation.eulerAngles}")} " +
            $"head={(camera == null ? "<none>" : $"{camera.transform.position} rot={camera.transform.rotation.eulerAngles}")} " +
            $"mount={(mount == null ? "<none>" : $"{mount.position} rot={mount.rotation.eulerAngles}")}");
        Debug.Log(
            "[NOVR] World map first renderer: " +
            (first == null
                ? "<none>"
                : $"{first.name} bounds={first.bounds} enabled={first.enabled} " +
                  $"material={first.sharedMaterial?.name} shader={first.sharedMaterial?.shader?.name}") +
            $"; overlay camera={(camera == null ? "<none>" : $"{camera.name} mask=0x{camera.cullingMask:X} far={camera.farClipPlane}")}");
    }

    private WorldMapModel? EnsureModel()
    {
        var levelInfo = NetworkSceneSingleton<LevelInfo>.i;
        var settings = levelInfo != null ? levelInfo.LoadedMapSettings : null;
        if (settings == null)
        {
            Hide();
            return null;
        }

        var detail = VrMapConfig.Detail != null ? VrMapConfig.Detail.Value : WorldMapDetail.Surfaces;

        // Rebuild when the mission moves to a different map — the clones hold
        // the old map's meshes, and the old map's prefab has been destroyed —
        // or when the pilot asks for a different amount of it.
        if (_model != null && (_model.Root == null || _model.Settings != settings || _model.Detail != detail))
        {
            _model.Destroy();
            _model = null;
        }

        return _model ??= WorldMapModel.Build(detail);
    }

    private void Hide()
    {
        if (_model?.Root != null) _model.Root.SetActive(false);
        ShowCockpit();
        ShowHelmetPanels();
        _reported = false;
    }

    /// <summary>
    /// Put the model under the aircraft, at scale, with the piece of map you are
    /// actually over directly below you — so the model slides beneath you as you
    /// fly, the way the ground does.
    ///
    /// <para><b>The frame this happens in is not the world's.</b> Everything on
    /// the VR UI layer is drawn by <c>VrCockpitHudCamera</c>, which is not in the
    /// aircraft at all: it hangs off the mod's own root near the world origin and
    /// is driven by the raw headset pose. The layer is a private room with the
    /// head at its centre, composited over the world afterwards. Measured: with
    /// the aircraft at (24.0, 18.8, -13.3) that camera was at (0.0, 0.0, 0.05).
    /// A model placed in world coordinates is therefore placed in the wrong room
    /// and simply is not there — which is exactly what the first build did.</para>
    ///
    /// <para>So the model goes into the room, and the aircraft's attitude has to
    /// be put back by hand: the room does not have it (the same thing the view
    /// icons were caught by on 08-13), so a model left at identity would be
    /// welded to the airframe and would roll with it. Countering the mount's
    /// rotation is what makes it behave like ground you are flying over.</para>
    /// </summary>
    private void Place(WorldMapModel model)
    {
        var room = NOUIManager.I != null ? NOUIManager.I.transform : null;
        var head = NOUIManager.I != null ? NOUIManager.I.CockpitHudCamera : null;
        var mount = Mount();
        if (room == null || head == null || mount == null) return;

        var scale = 1f / Mathf.Max(1f, VrMapConfig.Scale.Value);
        var exaggeration = Mathf.Max(1f, VrMapConfig.ReliefExaggeration.Value);
        var modelScale = new Vector3(scale, scale * exaggeration, scale);

        // World orientation as seen from inside the room. Local scale is applied
        // before this rotation, so the vertical exaggeration still runs along the
        // map's own up rather than the aircraft's.
        var toWorld = Quaternion.Inverse(mount.rotation);

        var root = model.Root.transform;
        if (root.parent != room) root.SetParent(room, false);
        root.localScale = modelScale;
        root.localRotation = toWorld;

        // Where we are on the map, in map metres: world position less the
        // floating origin, flattened to sea level.
        var here = mount.position - global::Datum.originPosition;
        var beneathUs = new Vector3(here.x, 0f, here.z);

        var headInRoom = room.InverseTransformPoint(head.transform.position);
        var down = toWorld * Vector3.down;
        root.localPosition = headInRoom
                             + down * VrMapConfig.EyeHeight.Value
                             - toWorld * Vector3.Scale(beneathUs, modelScale);

        PlaceMarker(root, beneathUs, modelScale);
    }

    /// <summary>
    /// Take the cockpit out of every camera that draws the aircraft, remembering
    /// what each had so it can be given back exactly. Culling masks rather than
    /// disabling the cameras: the cockpit and post-processing passes do other
    /// work, and a mask is a change that can be undone precisely.
    /// </summary>
    private void HideCockpit()
    {
        if (VrMapConfig.HideCockpit == null || !VrMapConfig.HideCockpit.Value)
        {
            ShowCockpit();
            return;
        }

        var mount = Mount();
        if (mount == null) return;

        foreach (var camera in mount.GetComponentsInChildren<Camera>(true))
        {
            if (!_maskedCameras.ContainsKey(camera)) _maskedCameras[camera] = camera.cullingMask;
            camera.cullingMask &= ~CockpitLayers;
        }
    }

    private void ShowCockpit()
    {
        if (_maskedCameras.Count == 0) return;
        foreach (var masked in _maskedCameras)
        {
            if (masked.Key != null) masked.Key.cullingMask = masked.Value;
        }

        _maskedCameras.Clear();
    }

    /// <summary>
    /// A plain cube, in world metres, sitting on the model exactly where the
    /// ground under the aircraft should be. It is a control: it uses the
    /// engine's default material rather than the game's terrain shader, and it
    /// is parented into the model like everything else. If the cube draws and
    /// the terrain does not, the geometry is in the right place and the shader
    /// is the problem; if neither draws, the problem is the model or the camera.
    /// </summary>
    private void PlaceMarker(Transform root, Vector3 beneathUs, Vector3 modelScale)
    {
        if (VrMapConfig.DebugMarker == null || !VrMapConfig.DebugMarker.Value)
        {
            if (_marker != null) _marker.SetActive(false);
            return;
        }

        if (_marker == null)
        {
            _marker = GameObject.CreatePrimitive(PrimitiveType.Cube);
            _marker.name = "World Map Debug Marker";
            Destroy(_marker.GetComponent<Collider>());
        }

        _marker.SetActive(true);
        var marker = _marker.transform;
        if (marker.parent != root) marker.SetParent(root, false);
        marker.localPosition = beneathUs;
        // Undo the model's scale so the cube is a fixed size in the room rather
        // than a fixed size on the map — at 1:1200 a map-sized cube would be
        // invisible and prove nothing.
        marker.localScale = new Vector3(3f / modelScale.x, 3f / modelScale.y, 3f / modelScale.z);
        _marker.layer = (int)LayerHelper.GetVrUiLayer();
    }

    /// <summary>
    /// The airframe-fixed mount — the same one the captured flight HUD hangs its
    /// panel from. Its position says where on the map we are; its rotation is
    /// the aircraft's attitude, which the model has to undo.
    /// </summary>
    private static Transform? Mount()
    {
        var mainCamera = APIBus.MainCamera;
        if (mainCamera == null) return null;
        return mainCamera.transform.parent != null ? mainCamera.transform.parent : mainCamera.transform;
    }
}
