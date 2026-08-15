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

    /// <summary>
    /// The real world outside the aircraft. Sky, sun and post-processing are
    /// deliberately not in here — taking those would leave the model floating in
    /// black, and the point is to be somewhere, just not in two places at once.
    /// </summary>
    private const int WorldLayers = (1 << (int)LayerHelper.Layers.Default)
                                    | (1 << (int)LayerHelper.Layers.Water)
                                    | (1 << (int)LayerHelper.Layers.Statics)
                                    | (1 << (int)LayerHelper.Layers.Effects)
                                    | (1 << (int)LayerHelper.Layers.Ship)
                                    | (1 << (int)LayerHelper.Layers.ExclusionZones);

    private WorldMapModel? _model;
    private WorldMapIcons? _iconLayer;
    private WorldMapSelfTest? _selfTest;
    private WorldMapPointer? _pointer;
    private bool _reported;
    private GameObject? _marker;
    private readonly Dictionary<Camera, int> _maskedCameras = new();
    private readonly List<HiddenPanel> _hiddenPanels = new();
    private bool _helmetTaken;
    private bool _iconsSeen;
    private float _iconsEmptySince;

    /// <summary>
    /// A panel we took out of view, and exactly what it takes to put it back.
    ///
    /// <para>A single component is switched off wherever one will do, and the
    /// GameObject only as a last resort. Two reasons, both learned the hard way:
    /// the game re-activates the map's GameObject on its own
    /// (<c>DynamicMap.EnableCanvas</c>), so deactivating it is a fight lost every
    /// frame; and a deactivated GameObject stops the behaviours under it running,
    /// which for the map means <c>DynamicMap.Update</c> no longer maintains the
    /// unit icons — the icon layer's entire source of truth. Hiding the map used
    /// to switch off the map the icons are read from.</para>
    /// </summary>
    private readonly struct HiddenPanel
    {
        public HiddenPanel(GameObject go, Behaviour? component)
        {
            Go = go;
            Component = component;
            WasActive = go.activeSelf;
            WasEnabled = component != null && component.enabled;
        }

        public readonly GameObject Go;
        public readonly Behaviour? Component;
        public readonly bool WasActive;
        public readonly bool WasEnabled;

        public void Hide()
        {
            if (Component != null)
            {
                if (Component.enabled) Component.enabled = false;
            }
            else if (Go != null && Go.activeSelf)
            {
                Go.SetActive(false);
            }
        }

        public void Restore()
        {
            if (Component != null) Component.enabled = WasEnabled;
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
        _iconLayer?.Clear();
        _iconLayer = null;
        _pointer?.Destroy();
        _pointer = null;
        if (_marker != null) Destroy(_marker);
        _marker = null;
        _model?.Destroy();
        _model = null;
    }

    private void Update()
    {
        if (VrMapConfig.ToggleShortcut != null && Input.GetKeyDown(VrMapConfig.ToggleShortcut.Value) ||
            WorldMapButtons.Pressed())
        {
            VrMapConfig.Open.Value = !VrMapConfig.Open.Value;
        }

        var open = VrMapConfig.Enabled != null && VrMapConfig.Enabled.Value &&
                   VrMapConfig.Open != null && VrMapConfig.Open.Value;

        // Before the early return, so the closed phase is timed too — the whole
        // point of the self test is the comparison between the two.
        if (VrMapConfig.SelfTest != null && VrMapConfig.SelfTest.Value)
        {
            (_selfTest ??= new WorldMapSelfTest()).Tick(open);
        }

        if (!open)
        {
            Hide();
            return;
        }

        // While the map is up, the surface being pointed at is the whole model
        // around you rather than a menu pinned in front of you, so head gaze goes
        // to the middle of the view and stays there. Amplification is the right
        // trade for a panel and the wrong one here: at 2x the cursor leaves the
        // centre twice as fast as the head does and then stops at the yaw clamp,
        // pinned to the edge of a rectangle that is no longer in front of you.
        // Asked for every frame, so closing the map gives it straight back.
        VrUiCursor.I?.UseViewCentreGaze();

        var model = EnsureModel();
        if (model == null) return;

        // Before the model is placed, because it is what decides where.
        WorldMapControls.Refresh(Mathf.Max(1f, VrMapConfig.Scale.Value));

        Place(model);
        model.Root.SetActive(true);
        RefreshIcons(model);
        HideCockpit();
        HideHelmetPanels();
        ReportOnce(model);
    }

    private void RefreshIcons(WorldMapModel model)
    {
        var room = NOUIManager.I != null ? NOUIManager.I.transform : null;
        var head = NOUIManager.I != null ? NOUIManager.I.CockpitHudCamera : null;
        if (room == null || head == null) return;

        if (VrMapConfig.Icons == null || !VrMapConfig.Icons.Value)
        {
            _iconLayer?.Clear();
            _iconLayer = null;
            return;
        }

        _iconLayer ??= new WorldMapIcons(room);
        _iconLayer.Refresh(
            model.Root.transform,
            head.transform,
            Mathf.Max(0.01f, VrMapConfig.IconSize.Value));

        ReportIcons(_iconLayer.Count);
        RefreshPointer(model, room);
    }

    /// <summary>
    /// Point at the model and select what you are pointing at. Sea level on the
    /// model is map y = 0 by construction — map coordinates are world less the
    /// floating origin, and the datum's own y is the sea.
    /// </summary>
    private void RefreshPointer(WorldMapModel model, Transform room)
    {
        if (VrMapConfig.Pointer == null || !VrMapConfig.Pointer.Value || _iconLayer == null)
        {
            _pointer?.Hide();
            return;
        }

        _pointer ??= new WorldMapPointer(room);
        _pointer.Refresh(_iconLayer, model.Root.transform, 0f);

        if (VrMapConfig.SelfTest != null && VrMapConfig.SelfTest.Value && _iconLayer.Count > 0)
        {
            _pointer.Sweep(_iconLayer);
            _pointer.VerifyClick(_iconLayer);
        }
    }

    /// <summary>
    /// Say once when units first appear, and complain once if they never do.
    /// The placement report fires on the first frame the map opens, which is
    /// too early to tell whether the icon layer works — it read "0 of 121" for
    /// a reason that had nothing to do with the icons.
    /// </summary>
    private void ReportIcons(int count)
    {
        if (_iconsSeen) return;

        if (count > 0)
        {
            _iconsSeen = true;
            var airbases = _iconLayer != null ? _iconLayer.Airbases : 0;
            Debug.Log($"[NOVR] World map: {count} icon(s) on the model — {count - airbases} unit(s) " +
                      $"of {UnitRegistry.allUnits.Count} in the mission, and {airbases} airbase(s).");
            return;
        }

        if (_iconsEmptySince == 0f) _iconsEmptySince = Time.unscaledTime;
        if (Time.unscaledTime - _iconsEmptySince < 5f) return;

        _iconsSeen = true;
        var map = SceneSingleton<global::DynamicMap>.i;
        Debug.LogWarning(
            $"[NOVR] World map: no unit icons after 5s with {UnitRegistry.allUnits.Count} unit(s) in the " +
            $"mission. DynamicMap={(map == null ? "<none>" : map.name + " active=" + map.gameObject.activeInHierarchy + " factor=" + map.mapDisplayFactor)}.");
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
    /// <para>Switched off a component at a time rather than faded. A CanvasGroup
    /// at alpha 0 took the weapon readout out and left the tactical map drawing,
    /// so something under the map does not inherit the group; and deactivating
    /// the objects instead stopped <c>DynamicMap.Update</c>, which is what keeps
    /// the unit icons this map draws alive. See <see cref="HiddenPanel"/>.</para>
    /// </summary>
    private void HideHelmetPanels()
    {
        if (VrMapConfig.HideHelmetPanels == null || !VrMapConfig.HideHelmetPanels.Value)
        {
            ShowHelmetPanels();
            return;
        }

        if (!_helmetTaken) TakeHelmetPanels();

        // Every frame, not once. The game turns the map's GameObject back on by
        // itself, so a single hide is undone before it is ever seen.
        foreach (var panel in _hiddenPanels) panel.Hide();
    }

    /// <summary>
    /// Work out what to switch off, once. Each of the three needs a different
    /// mechanism, and using one rule for all of them broke two of them:
    /// deactivating everything stopped the map updating its unit icons, and
    /// disabling one component each left the weapon readout's symbols drawn over
    /// a hole where its backing had been.
    /// </summary>
    private void TakeHelmetPanels()
    {
        var hmd = SceneSingleton<HeadMountedDisplay>.i;
        var helmet = hmd != null ? hmd.transform : null;
        var map = SceneSingleton<global::DynamicMap>.i;
        if (helmet == null || map == null) return;

        // The weapon readout: deactivated outright. Nothing under it has to keep
        // running, and it is the only way to take both its symbols and the panel
        // they sit on.
        foreach (Transform child in helmet)
        {
            if (child.GetComponentInChildren<WeaponStatus>(true) == null) continue;
            Take(child.gameObject, null, "weapon readout");
        }

        // The map itself: its Canvas, so DynamicMap keeps updating underneath —
        // it is where the unit icons come from.
        Take(map.gameObject, map.GetComponent<Canvas>(), "tactical map");

        // The dark backing: not part of the map object and not `mapBackground`
        // either (measured — disabling that changed nothing). It is drawn by the
        // helmet child the map is anchored to, so take every graphic under that
        // child except the map's own, which is already handled and whose icon
        // images this feature reads.
        foreach (Transform child in helmet)
        {
            if (map.hudMapAnchor == null || !map.hudMapAnchor.IsChildOf(child)) continue;
            foreach (var graphic in child.GetComponentsInChildren<UnityEngine.UI.Graphic>(true))
            {
                if (graphic == null || graphic.transform.IsChildOf(map.transform)) continue;
                Take(graphic.gameObject, graphic, "tactical map backing");
            }

            break;
        }

        _helmetTaken = true;
    }

    private void Take(GameObject panel, Behaviour? component, string what)
    {
        foreach (var known in _hiddenPanels)
        {
            if (known.Go == panel) return;
        }

        _hiddenPanels.Add(new HiddenPanel(panel, component));
        Debug.Log(
            $"[NOVR] World map: hiding the {what} ('{panel.name}') by " +
            (component != null ? "disabling its " + component.GetType().Name : "deactivating it") + ".");
    }

    private void ShowHelmetPanels()
    {
        _helmetTaken = false;
        if (_hiddenPanels.Count == 0) return;
        foreach (var panel in _hiddenPanels) panel.Restore();
        VerifyRestored();
        _hiddenPanels.Clear();
    }

    /// <summary>
    /// Say, on the frame the map closes, whether everything it took is actually
    /// back — each panel in the state it was found in, and each camera drawing
    /// the layers it was drawing.
    ///
    /// <para>The restore path is symmetric in code, which is exactly the kind of
    /// thing that is believed rather than checked. It is checked here because
    /// the failure is silent and permanent: a mask left masked means the world
    /// never comes back, and nothing in the frame says why.</para>
    /// </summary>
    private void VerifyRestored()
    {
        if (VrMapConfig.SelfTest == null || !VrMapConfig.SelfTest.Value) return;

        var wrong = 0;
        foreach (var panel in _hiddenPanels)
        {
            var back = panel.Component != null
                ? panel.Component.enabled == panel.WasEnabled
                : panel.Go != null && panel.Go.activeSelf == panel.WasActive;
            if (back) continue;
            wrong++;
            Debug.LogWarning($"[NOVR] World map self test: '{panel.Go?.name}' did not come back.");
        }

        Debug.Log($"[NOVR] World map self test: closed — {_hiddenPanels.Count - wrong} of " +
                  $"{_hiddenPanels.Count} panel(s) back, icon layer " +
                  $"{(_iconLayer == null ? "gone" : "hidden")}.");
    }

    /// <summary>
    /// The same check for the cameras, run before their masks are forgotten:
    /// every layer this map took away is being drawn again.
    /// </summary>
    private void VerifyCameras()
    {
        if (VrMapConfig.SelfTest == null || !VrMapConfig.SelfTest.Value) return;

        var wrong = 0;
        var taken = CockpitLayers | WorldLayers;
        foreach (var camera in _maskedCameras)
        {
            if (camera.Key == null) continue;
            if ((camera.Key.cullingMask & taken) != (camera.Value & taken)) wrong++;
        }

        Debug.Log($"[NOVR] World map self test: {_maskedCameras.Count - wrong} of " +
                  $"{_maskedCameras.Count} camera(s) drawing the layers they were.");
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
            $"rot={root.localRotation.eulerAngles} orientation={(VrMapConfig.Orientation != null ? VrMapConfig.Orientation.Value.ToString() : "?")} " +
            $"aircraft rot={(mount != null ? mount.rotation.eulerAngles.ToString() : "<none>")} " +
            $"layer={model.Root.layer} active={model.Root.activeInHierarchy} " +
            $"icons={(_iconLayer == null ? "off" : _iconLayer.Count + " of " + UnitRegistry.allUnits.Count + " units")} " +
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
        var sea = VrMapConfig.Sea == null || VrMapConfig.Sea.Value;

        // Rebuild when the mission moves to a different map — the clones hold
        // the old map's meshes, and the old map's prefab has been destroyed —
        // or when the pilot asks for a different amount of it.
        if (_model != null && (_model.Root == null || _model.Settings != settings ||
                               _model.Detail != detail || _model.HasSea != sea))
        {
            _model.Destroy();
            _model = null;
        }

        return _model ??= WorldMapModel.Build(detail);
    }

    private void Hide()
    {
        if (_model?.Root != null) _model.Root.SetActive(false);
        _iconLayer?.SetVisible(false);
        _pointer?.Hide();
        // Before anything else that can fail: the aeroplane gets its controls
        // back on the frame the map goes away, not on the frame the rest of the
        // teardown happens to finish.
        WorldMapControls.Release();
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
    /// <summary>
    /// The rotation that takes map directions into the room, for the orientation
    /// the pilot asked for. <c>WorldFixed</c> is the inverse of the whole aircraft
    /// attitude; <c>TrackUp</c> keeps only its heading; <c>NorthUp</c> is nothing
    /// at all, which is what makes it immovable.
    /// </summary>
    private static Quaternion Turn(Transform mount)
    {
        var orientation = VrMapConfig.Orientation != null
            ? VrMapConfig.Orientation.Value
            : WorldMapOrientation.NorthUp;

        switch (orientation)
        {
            case WorldMapOrientation.WorldFixed:
                return Quaternion.Inverse(mount.rotation);
            case WorldMapOrientation.TrackUp:
                // eulerAngles.y off a rotation with roll in it is not the heading;
                // the flattened forward is, and it stays right upside down.
                var forward = mount.forward;
                var flat = new Vector3(forward.x, 0f, forward.z);
                if (flat.sqrMagnitude < 1e-6f)
                {
                    // Pointing straight up or down: the nose says nothing about
                    // heading, so take it from where the top of the head faces.
                    var up = -mount.up * Mathf.Sign(forward.y);
                    flat = new Vector3(up.x, 0f, up.z);
                    if (flat.sqrMagnitude < 1e-6f) return Quaternion.identity;
                }

                return Quaternion.Inverse(Quaternion.LookRotation(flat.normalized, Vector3.up));
            default:
                return Quaternion.identity;
        }
    }

    private void Place(WorldMapModel model)
    {
        var room = NOUIManager.I != null ? NOUIManager.I.transform : null;
        var head = NOUIManager.I != null ? NOUIManager.I.CockpitHudCamera : null;
        var mount = Mount();
        if (room == null || head == null || mount == null) return;

        var scale = 1f / Mathf.Max(1f, VrMapConfig.Scale.Value);
        var exaggeration = Mathf.Max(1f, VrMapConfig.ReliefExaggeration.Value);
        var modelScale = new Vector3(scale, scale * exaggeration, scale);

        // How the model is turned inside the room. Local scale is applied before
        // this rotation, so the vertical exaggeration still runs along the map's
        // own up rather than the aircraft's.
        //
        // The room is not attached to the aircraft — it sits near the world origin
        // with the raw headset pose inside it — so a model left unrotated is fixed
        // to the physical room and the aircraft cannot disturb it. Countering the
        // aircraft's whole attitude, which is what this did, pins the model to the
        // world instead, and since your head is in the aircraft the model then
        // rolls and pitches against you on every stick input. That reads exactly
        // like being turned around by the aeroplane, which is what a flight
        // reported. Heading alone is the middle: aligned with where you are going,
        // undisturbed by how you are flying.
        var toWorld = Turn(mount);

        // Whatever the stick has pushed the map to, on top of the orientation. The
        // spin is applied outside the orientation so it turns the model about the
        // room's own vertical — which, given the placement below, is the vertical
        // through the point under your head.
        var turn = Quaternion.Euler(0f, WorldMapControls.Spin, 0f) * toWorld;

        var root = model.Root.transform;
        if (root.parent != room) root.SetParent(room, false);
        root.localScale = modelScale;
        root.localRotation = turn;

        // Where we are on the map, in map metres: world position less the floating
        // origin, flattened to sea level — then wherever the map has been panned
        // to from there. This point is the one held under the head, so it is both
        // what panning moves and what spinning turns about.
        var here = mount.position - global::Datum.originPosition;
        var pan = WorldMapControls.Pan;
        var beneathUs = new Vector3(here.x + pan.x, 0f, here.z + pan.y);

        var headInRoom = room.InverseTransformPoint(head.transform.position);
        var down = turn * Vector3.down;
        root.localPosition = headInRoom
                             + down * VrMapConfig.EyeHeight.Value
                             - turn * Vector3.Scale(beneathUs, modelScale);

        // The marker still belongs on the aircraft, not on wherever the map has
        // been pushed to.
        PlaceMarker(root, new Vector3(here.x, 0f, here.z), modelScale);
    }

    /// <summary>
    /// Take what the map is meant to replace out of every camera that draws the
    /// aircraft, remembering what each had so it can be given back exactly.
    /// Culling masks rather than disabling the cameras: the cockpit and
    /// post-processing passes do other work, and a mask is a change that can be
    /// undone precisely.
    /// </summary>
    private void HideCockpit()
    {
        var hide = 0;
        if (VrMapConfig.HideCockpit != null && VrMapConfig.HideCockpit.Value) hide |= CockpitLayers;
        if (VrMapConfig.HideWorld != null && VrMapConfig.HideWorld.Value) hide |= WorldLayers;

        if (hide == 0)
        {
            ShowCockpit();
            return;
        }

        var mount = Mount();
        if (mount == null) return;

        foreach (var camera in mount.GetComponentsInChildren<Camera>(true))
        {
            if (!_maskedCameras.ContainsKey(camera)) _maskedCameras[camera] = camera.cullingMask;
            camera.cullingMask = _maskedCameras[camera] & ~hide;
        }
    }

    private void ShowCockpit()
    {
        if (_maskedCameras.Count == 0) return;
        foreach (var masked in _maskedCameras)
        {
            if (masked.Key != null) masked.Key.cullingMask = masked.Value;
        }

        VerifyCameras();
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
