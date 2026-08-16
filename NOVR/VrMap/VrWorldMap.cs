using System.Collections.Generic;
using NOVR.VrUi;
using UnityEngine;

namespace NOVR.VrMap;

/// <summary>
/// The 3D world map: the whole theatre as a solid model of the real terrain —
/// standing in front of you in the place the game's own map goes, or laid out
/// below you as a diorama you fly over — and the shortcut that shows and hides
/// it. See <see cref="WorldMapPlacement"/>: those are not two ways of drawing
/// the same thing, and most of what this class does depends on which is up.
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
/// <para><b>Why it does not follow your head.</b> Neither placement is anchored
/// to the head: a model anchored to the head moves when you lean, and it is the
/// leaning that makes it solid — a wall that moves with you shows both eyes and
/// both head positions the same picture, which is the flat map again. The table
/// is placed against the airframe mount and the wall is pinned where you were
/// facing when it opened; both stay put while you look around them, and north
/// stays north on both unless you ask otherwise.</para>
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

    private WorldMapHaze? _haze;
    private bool _menuWasUp;
    private bool _reported;
    private GameObject? _marker;
    private readonly Dictionary<Camera, int> _maskedCameras = new();
    private readonly List<HiddenPanel> _hiddenPanels = new();
    private bool _helmetTaken;
    private bool _hidingMapOnly;
    private bool _gameMapWasUp;

    /// <summary>
    /// Which way the wall faces, in room coordinates — latched when the map opens
    /// so the model stays where it was hung while you look around it.
    /// </summary>
    private Vector3? _wallFacing;

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
        public HiddenPanel(GameObject go, Behaviour? component, bool isMap)
        {
            Go = go;
            Component = component;
            IsMap = isMap;
            WasActive = go.activeSelf;
            WasEnabled = component != null && component.enabled;
        }

        public readonly GameObject Go;
        public readonly Behaviour? Component;

        /// <summary>
        /// The flat map itself or its backing, as opposed to the weapon readout.
        /// The wall replaces the map and only the map: the pilot is still flying,
        /// and taking their weapon state away to show them a map is not a trade
        /// anyone asked for.
        /// </summary>
        public readonly bool IsMap;
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
        _haze?.Destroy();
        _haze = null;
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

        // The game's own map is a second switch, not just a second source of pan
        // and zoom: on the wall this model stands where the flat map stands and
        // hides it, so "the map is open" has to mean the same thing for both or
        // the pilot gets one of them without the other. It also fixes the way the
        // two used to drift apart — F10 minimizing the game's map while this
        // mod's own Open flag stayed set, which is how a flight ended up looking
        // at the ground through a map that was still notionally up.
        var gameMapUp = TheGameOpenedIt();

        // Closing the game's map closes this one, however it was closed —
        // Escape, a rebound key, or the game deciding for itself. Without this the
        // two switches drift apart in the one direction the shortcut cannot fix,
        // and the map is left notionally open with nothing in it, which is the
        // state a flight reported as "F10 showed me the ground".
        if (_gameMapWasUp && !gameMapUp && VrMapConfig.Open != null && VrMapConfig.Open.Value)
        {
            VrMapConfig.Open.Value = false;
        }

        _gameMapWasUp = gameMapUp;

        var open = VrMapConfig.Enabled != null && VrMapConfig.Enabled.Value &&
                   (VrMapConfig.Open != null && VrMapConfig.Open.Value || gameMapUp);

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

        var sharing = SomethingElseWantsThePointer();

        // While the map has the view to itself, the surface being pointed at is
        // the whole model around you rather than a menu pinned in front of you, so
        // head gaze goes to the middle of the view and stays there. Amplification
        // is the right trade for a panel and the wrong one here: at 2x the cursor
        // leaves the centre twice as fast as the head does and then stops at the
        // yaw clamp, pinned to the edge of a rectangle that is no longer in front
        // of you. Asked for every frame, so closing the map — or something else
        // asking for the pointer — gives it straight back.
        // Only the table, which is a place you have gone to: the cursor is meant
        // to be able to reach anywhere around you there. The wall is a panel in a
        // cockpit you never left, and the pilot's cursor is still the game's to
        // aim at the game's own screens.
        if (!sharing && !OnTheWall) VrUiCursor.I?.UseViewCentreGaze();

        var model = EnsureModel();
        if (model == null) return;

        // Before the model is placed, because it is what decides where — and how
        // big, since the sticks zoom as well as pan. The theatre's width goes with
        // it because the wall is scaled to fit the map rather than to a ratio.
        WorldMapControls.TheatreWidth = Mathf.Max(model.MapSize.x, model.MapSize.y);
        WorldMapControls.Refresh(takeControls: !sharing);

        Place(model);
        model.Root.SetActive(true);
        RefreshIcons(model, sharing);

        if (OnTheWall)
        {
            // The wall replaces the flat map and nothing else. You have not left
            // the aircraft — the cockpit stays, the outside stays, and the stick
            // stays yours — so the only thing taken is the map this one is
            // standing in for. That holds whether or not something else wants the
            // pointer: a menu in front of the wall is a menu in front of a wall.
            ShowCockpit();
            HideHelmetPanels(mapOnly: true);
        }
        else if (sharing)
        {
            // Everything that takes something away from the rest of the game is
            // given back: the game's own screen has to look exactly as it does
            // without this map, because that is the screen being used.
            ShowCockpit();
            ShowHelmetPanels();
        }
        else
        {
            HideCockpit();
            HideHelmetPanels();
        }

        ReportOnce(model);
    }

    /// <summary>
    /// Whether something else wants the pointer, in which case the map shares the
    /// view rather than running it.
    ///
    /// <para><b>What is given up, and what is not.</b> The map normally takes four
    /// things to be the world around you: the cursor is driven from the middle of
    /// your view so you can point anywhere, the helmet panels are hidden, the
    /// cockpit and the outside are culled, and the stick comes off the aeroplane.
    /// Pointer UI wants the opposite of all four — a cursor amplified against a
    /// panel pinned in front of you, and everything else exactly as the flat game
    /// leaves it. So all four are handed back, and the model keeps being drawn.
    /// The thumbsticks are not handed back, because they are nobody else's.</para>
    ///
    /// <para><b>Why not simply stand down, which is what the last version
    /// did.</b> Because the model is the one thing here that does not conflict
    /// with a pointer: it is geometry in a room, and a panel in front of it is a
    /// panel in front of it. Hiding it took the map away exactly when it is most
    /// wanted — a flight reported not being able to open it at all during airbase
    /// selection, which is the moment you would most like to look at the theatre,
    /// and the shortcut did nothing because the suspend re-closed it on the same
    /// frame. The original complaint was never about the model being drawn: it was
    /// two cursors, one of which would select map symbols and neither of which
    /// would press "select airbase". That is fixed by giving the cursor back, and
    /// giving the cursor back does not need the map gone.</para>
    ///
    /// <para><b>Asked as "does the game want a mouse", which is the game's own
    /// question.</b> <c>CursorManager</c> carries a flag per reason — the map
    /// being maximized is one of them, and <c>DynamicMap.Maximize</c> sets it in
    /// the same breath as it calls <c>GameplayUI.ShowSelectAirbase</c>. So the
    /// spawn screen, the flat map and every menu that wants pointing at all
    /// answer yes together, and nothing has to be enumerated. The captured-menu
    /// backend is asked as well, because it keys on the menu scene's own canvas
    /// and can be up in cases the cursor flags are not.</para>
    /// </summary>
    private bool SomethingElseWantsThePointer()
    {
        string? why = null;
        if (NOVR.VrUi.Capture.MenuCaptureBackend.IsActive) why = "a menu is up";
        else if (WantsAPointer()) why = "the game has asked for the mouse pointer";

        var up = why != null;
        if (up == _menuWasUp) return up;

        _menuWasUp = up;
        if (VrMapConfig.Open != null && VrMapConfig.Open.Value)
        {
            Debug.Log(up
                ? $"[NOVR] World map: {why} — sharing the view: the model stays, the cursor, " +
                  "the pointer, the cockpit and the stick go back to the game."
                : "[NOVR] World map: the view is ours again — taking the cursor and the cockpit back.");
        }

        return up;
    }

    /// <summary>
    /// The game's own cursor state, with a fallback for the case where its
    /// manager is not reachable. Both mean the same thing: a pointer is on
    /// screen, so a pointer is what the player is meant to be using.
    /// </summary>
    private static bool WantsAPointer()
    {
        try
        {
            if (CursorManager.GetFlags() != 0) return true;
        }
        catch (System.Exception)
        {
            // Not worth a warning every frame: the fallback below is the same
            // question asked of Unity instead of the game.
        }

        return Cursor.visible && Cursor.lockState != CursorLockMode.Locked;
    }

    private void RefreshIcons(WorldMapModel model, bool sharing)
    {
        var room = NOUIManager.I != null ? NOUIManager.I.transform : null;
        var head = NOUIManager.I != null ? NOUIManager.I.CockpitHudCamera : null;
        if (room == null || head == null) return;

        ApplyHaze(model, head.transform);

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
        RefreshPointer(model, room, sharing);
    }

    /// <summary>
    /// Point at the model and select what you are pointing at. Sea level on the
    /// model is map y = 0 by construction — map coordinates are world less the
    /// floating origin, and the datum's own y is the sea.
    /// </summary>
    private void RefreshPointer(WorldMapModel model, Transform room, bool sharing)
    {
        // Not while the game wants the pointer for itself — on the table. A ring
        // running over the terrain is the half of this feature that competes with
        // a button: it is the second thing claiming what the trigger means.
        //
        // The wall is the other way round, and has to be: it hides the flat map,
        // so the icons on the flat map cannot be clicked while it is up, and this
        // ring is then the only way to reach a symbol at all. It does not compete
        // with the mouse either — it is on the VR trigger, and the wall leaves the
        // pilot's cursor exactly where the game put it. Note that the wall is
        // almost always "sharing": opening the game's map sets the cursor's own
        // Map flag, so the question answers yes for the whole time the wall is up.
        if (sharing && !OnTheWall ||
            VrMapConfig.Pointer == null || !VrMapConfig.Pointer.Value || _iconLayer == null)
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
    /// <param name="mapOnly">
    /// Take the flat map and its backing and leave the weapon readout alone —
    /// what the wall wants, because the wall replaces the map without taking the
    /// pilot out of the aircraft. The table takes both: there is no aircraft
    /// around you to read a weapon state off.
    /// </param>
    private void HideHelmetPanels(bool mapOnly = false)
    {
        if (VrMapConfig.HideHelmetPanels == null || !VrMapConfig.HideHelmetPanels.Value)
        {
            ShowHelmetPanels();
            return;
        }

        if (!_helmetTaken) TakeHelmetPanels();

        // Once, on the frame the answer changes — not every frame, which would be
        // this feature and the game both writing the same flag forever.
        if (mapOnly != _hidingMapOnly)
        {
            _hidingMapOnly = mapOnly;
            if (mapOnly)
            {
                foreach (var panel in _hiddenPanels)
                {
                    if (!panel.IsMap) panel.Restore();
                }
            }
        }

        // Every frame, not once. The game turns the map's GameObject back on by
        // itself, so a single hide is undone before it is ever seen.
        foreach (var panel in _hiddenPanels)
        {
            if (mapOnly && !panel.IsMap) continue;
            panel.Hide();
        }
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
            Take(child.gameObject, null, "weapon readout", isMap: false);
        }

        // The map itself: its Canvas, so DynamicMap keeps updating underneath —
        // it is where the unit icons come from.
        Take(map.gameObject, map.GetComponent<Canvas>(), "tactical map", isMap: true);

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
                Take(graphic.gameObject, graphic, "tactical map backing", isMap: true);
            }

            break;
        }

        _helmetTaken = true;
    }

    private void Take(GameObject panel, Behaviour? component, string what, bool isMap)
    {
        foreach (var known in _hiddenPanels)
        {
            if (known.Go == panel) return;
        }

        _hiddenPanels.Add(new HiddenPanel(panel, component, isMap));
        Debug.Log(
            $"[NOVR] World map: hiding the {what} ('{panel.name}') by " +
            (component != null ? "disabling its " + component.GetType().Name : "deactivating it") + ".");
    }

    private void ShowHelmetPanels()
    {
        _helmetTaken = false;
        _hidingMapOnly = false;
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
        ReportOrientations();

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
        _haze?.SetVisible(false);
        ShowCockpit();
        ShowHelmetPanels();
        // Forgotten on close so reopening re-hangs the wall in front of wherever
        // the pilot is facing then. That is the whole recentre gesture.
        _wallFacing = null;
        _reported = false;
    }

    /// <summary>
    /// The rotation that takes map directions into the room, for the orientation
    /// the pilot asked for. <c>WorldFixed</c> is the inverse of the whole aircraft
    /// attitude; <c>TrackUp</c> keeps only its heading; <c>NorthUp</c> is nothing
    /// at all, which is what makes it immovable.
    /// </summary>
    /// <summary>
    /// What each orientation would do to a handful of attitudes, including ones
    /// the harness cannot fly. The interesting cases are all about roll: an
    /// aircraft in a 60° bank on a heading of 090 must still give Track Up a clean
    /// 090, and <c>eulerAngles.y</c> does not — which is why the heading comes off
    /// the flattened nose instead. Inverted flight and a vertical climb are the
    /// two that break naive versions of both.
    /// </summary>
    private static void ReportOrientations()
    {
        if (VrMapConfig.SelfTest == null || !VrMapConfig.SelfTest.Value) return;

        var cases = new (string Name, Quaternion Attitude)[]
        {
            ("level, heading 090", Quaternion.Euler(0f, 90f, 0f)),
            ("60 deg bank, heading 090", Quaternion.Euler(0f, 90f, 60f)),
            ("20 deg climb, 30 deg bank, heading 270", Quaternion.Euler(-20f, 270f, 30f)),
            ("inverted, heading 180", Quaternion.Euler(0f, 180f, 180f)),
            ("vertical climb, rolled to heading 045", Quaternion.Euler(-90f, 0f, 0f) * Quaternion.Euler(0f, 0f, 45f))
        };

        foreach (var (name, attitude) in cases)
        {
            // The heading the model ends up showing: undo the map's rotation and
            // read where the aircraft points on it. Straight up or down there is
            // no nose heading to read — the measure is undefined, not the answer —
            // so fall back to where the canopy faces, which is what a pilot means
            // by heading in a vertical.
            var track = Turn(attitude, WorldMapOrientation.TrackUp);
            var nose = track * (attitude * Vector3.forward);
            var vertical = new Vector2(nose.x, nose.z).sqrMagnitude < 0.01f;
            var read = vertical ? track * (attitude * Vector3.down) : nose;
            var shown = Mathf.Atan2(read.x, read.z) * Mathf.Rad2Deg;

            Debug.Log($"[NOVR] World map orientation, {name}: " +
                      $"NorthUp {Turn(attitude, WorldMapOrientation.NorthUp).eulerAngles}, " +
                      $"TrackUp {track.eulerAngles}, " +
                      $"WorldFixed {Turn(attitude, WorldMapOrientation.WorldFixed).eulerAngles}; " +
                      $"under TrackUp the {(vertical ? "canopy (the nose is vertical and has no heading)" : "nose")} " +
                      $"points {shown:F1}° on the model " +
                      "(0 means straight away from you, and is the only right answer).");
        }
    }

    private static Quaternion Turn(Transform mount) => Turn(
        mount.rotation,
        VrMapConfig.Orientation != null ? VrMapConfig.Orientation.Value : WorldMapOrientation.NorthUp);

    private static Quaternion Turn(Quaternion attitude, WorldMapOrientation orientation)
    {
        switch (orientation)
        {
            case WorldMapOrientation.WorldFixed:
                return Quaternion.Inverse(attitude);
            case WorldMapOrientation.TrackUp:
                // eulerAngles.y off a rotation with roll in it is not the heading;
                // the flattened forward is, and it stays right upside down.
                var forward = attitude * Vector3.forward;
                var flat = new Vector3(forward.x, 0f, forward.z);
                if (flat.sqrMagnitude < 1e-6f)
                {
                    // Pointing straight up or down: the nose says nothing about
                    // heading, so take it from where the top of the head faces.
                    var up = -(attitude * Vector3.up) * Mathf.Sign(forward.y);
                    flat = new Vector3(up.x, 0f, up.z);
                    if (flat.sqrMagnitude < 1e-6f) return Quaternion.identity;
                }

                return Quaternion.Inverse(Quaternion.LookRotation(flat.normalized, Vector3.up));
            default:
                return Quaternion.identity;
        }
    }

    /// <summary>
    /// Put air between you and the far side of the model.
    ///
    /// <para>Unity's own fog was the obvious way and does not work here: the
    /// game's terrain shader ignores <c>RenderSettings</c> entirely. Measured —
    /// the range set to 5 km and to 40 km, an eightfold change that should have
    /// been a white-out against a clear day, returned frames identical to a tenth
    /// of a grey level. So the air is geometry instead; see
    /// <see cref="WorldMapHaze"/> for what it is made of, why it lies in flat
    /// layers rather than wrapping round your head, and why the depth buffer is
    /// the only thing it needs.</para>
    ///
    /// <para>Everything is handed over in map metres, because that is the frame
    /// the layers live in: they are children of the model, so the scale, the
    /// panning and the spin are already applied to them.</para>
    /// </summary>
    private void ApplyHaze(WorldMapModel model, Transform head)
    {
        if (VrMapConfig.Haze == null || !VrMapConfig.Haze.Value)
        {
            _haze?.SetVisible(false);
            return;
        }

        var rangeKm = VrMapConfig.HazeRange != null ? VrMapConfig.HazeRange.Value : 40f;
        var strength = VrMapConfig.HazeStrength != null ? VrMapConfig.HazeStrength.Value : 0.85f;
        var ceiling = VrMapConfig.HazeCeiling != null ? VrMapConfig.HazeCeiling.Value : 2500f;

        _haze ??= new WorldMapHaze();
        _haze.Refresh(
            model.Root.transform, head, model.MapSize, ceiling, rangeKm * 1000f, HazeColour(), strength);
    }

    /// <summary>
    /// The game's own distance haze, if it has one worth borrowing — it does:
    /// the scene ships exponential fog at a colour its artist chose, which is the
    /// air the real theatre recedes into even though the terrain shader never
    /// reads it. Black is the engine default and means nobody set anything, so
    /// that one is replaced rather than trusted.
    /// </summary>
    private static Color HazeColour()
    {
        var scene = RenderSettings.fogColor;
        var brightest = Mathf.Max(scene.r, Mathf.Max(scene.g, scene.b));
        if (brightest > 0.2f)
        {
            // The scene's is an HDR colour (measured: 0.79, 1.11, 1.44). Bring it
            // back into range without losing the hue, which is the part that
            // matters — haze is blue because the sky is.
            var over = Mathf.Max(1f, brightest);
            return new Color(scene.r / over, scene.g / over, scene.b / over, 1f);
        }

        return new Color(0.72f, 0.82f, 0.90f, 1f);
    }

    /// <summary>
    /// Put the model where the pilot asked for it — standing in front of them or
    /// lying below them — at scale, with the piece of map they are reading in the
    /// middle of it.
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
    /// <para>So the model goes into the room, and anything the aircraft is meant
    /// to contribute has to be put back by hand: the room does not have the
    /// airframe's attitude (the same thing the view icons were caught by on
    /// 08-13), which is why the orientation modes exist at all.</para>
    /// </summary>
    private void Place(WorldMapModel model)
    {
        var room = NOUIManager.I != null ? NOUIManager.I.transform : null;
        var head = NOUIManager.I != null ? NOUIManager.I.CockpitHudCamera : null;
        var mount = Mount();
        if (room == null || head == null || mount == null) return;

        var scale = 1f / WorldMapControls.MapScale;
        var exaggeration = Mathf.Max(1f, VrMapConfig.ReliefExaggeration.Value);
        var modelScale = new Vector3(scale, scale * exaggeration, scale);

        // Where we are on the map, in map metres: world position less the floating
        // origin, flattened to sea level — then wherever the map has been panned
        // to from there. When the game's own map is driving it hands over the
        // centre outright instead, because that is what it knows: its offsets are
        // absolute and have already had the aircraft's position folded in.
        var here = mount.position - global::Datum.originPosition;
        var centre = WorldMapControls.Centre is { } absolute
            ? new Vector3(absolute.x, 0f, absolute.y)
            : new Vector3(here.x + WorldMapControls.Pan.x, 0f, here.z + WorldMapControls.Pan.y);

        if (OnTheWall) PlaceWall(model, room, head.transform, mount, modelScale, centre);
        else PlaceTable(model, room, head.transform, mount, modelScale, centre);

        // The marker still belongs on the aircraft, not on wherever the map has
        // been pushed to.
        PlaceMarker(model.Root.transform, new Vector3(here.x, 0f, here.z), modelScale);
    }

    /// <summary>
    /// Stand the model up in front of the pilot: north up the wall, east across
    /// it, and the terrain's own vertical pointing back out at them, so the
    /// mountains stand out of the map the way they stand out of the ground.
    ///
    /// <para><b>Why there is no frame and nothing is clipped.</b> The flat map is
    /// a picture inside a rectangle, so zooming in has to crop. This is not a
    /// picture — it is geometry — and the model's materials are the game's own
    /// terrain shader, which has no clip plane to hand and cannot be given one
    /// without giving up the art that makes the model worth looking at. So
    /// zooming enlarges the model about the point in the middle of the wall and
    /// the rest simply extends past the edge of what you can see, which is what a
    /// hologram would do and costs nothing to draw: a vertical plane grows
    /// sideways, never towards you, so however far it is zoomed it cannot end up
    /// in the cockpit with you.</para>
    /// </summary>
    private void PlaceWall(
        WorldMapModel model, Transform room, Transform head, Transform mount,
        Vector3 modelScale, Vector3 centre)
    {
        var facing = WallFacing(room, head);

        // The orientation modes and the stick's spin are both a yaw about the
        // map's own vertical, which is the axis pointing out of the wall — so they
        // are applied in map space, inside the rotation that stands it up, rather
        // than in the room.
        var yaw = Turn(mount).eulerAngles.y + WorldMapControls.Spin;
        var rot = Quaternion.LookRotation(Vector3.up, -facing) * Quaternion.Euler(0f, yaw, 0f);

        var root = model.Root.transform;
        if (root.parent != room) root.SetParent(room, false);
        root.localScale = modelScale;
        root.localRotation = rot;

        var distance = VrMapConfig.WallDistance != null ? VrMapConfig.WallDistance.Value : 3f;
        var wallCentre = room.InverseTransformPoint(head.position) + facing * distance;
        root.localPosition = wallCentre - rot * Vector3.Scale(centre, modelScale);
    }

    /// <summary>
    /// Which way the wall faces, in room coordinates: level, and — unless the
    /// pilot has asked otherwise — latched the first frame the map is up.
    ///
    /// <para>A wall that follows the head cannot be leaned into, and leaning into
    /// it is the whole difference between a hologram and a screen: a model that
    /// moves with you gives your two eyes and your two head positions the same
    /// picture, which is the flat map again with extra steps. Pinned, it stays
    /// where it was hung and you look around it. Closing and reopening re-hangs
    /// it, which is the same recentre gesture the rest of this feature uses.</para>
    /// </summary>
    private Vector3 WallFacing(Transform room, Transform head)
    {
        var follows = VrMapConfig.WallFollowsHead != null && VrMapConfig.WallFollowsHead.Value;
        if (!follows && _wallFacing.HasValue) return _wallFacing.Value;

        var forward = room.InverseTransformDirection(head.forward);
        var flat = new Vector3(forward.x, 0f, forward.z);
        if (flat.sqrMagnitude < 1e-6f)
        {
            // Head tipped straight up or straight down: where the nose points says
            // nothing about which way is ahead, so take it from the top of the
            // head, the same way the track-up orientation does in a vertical.
            var up = room.InverseTransformDirection(head.up) * -Mathf.Sign(forward.y);
            flat = new Vector3(up.x, 0f, up.z);
            if (flat.sqrMagnitude < 1e-6f) flat = Vector3.forward;
        }

        var facing = flat.normalized;
        if (!follows) _wallFacing = facing;
        return facing;
    }

    private void PlaceTable(
        WorldMapModel model, Transform room, Transform head, Transform mount,
        Vector3 modelScale, Vector3 centre)
    {
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

        // The centre is the point held under the head, so it is both what panning
        // moves and what spinning turns about.
        var headInRoom = room.InverseTransformPoint(head.position);
        var down = turn * Vector3.down;
        root.localPosition = headInRoom
                             + down * VrMapConfig.EyeHeight.Value
                             - turn * Vector3.Scale(centre, modelScale);
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
    /// <summary>
    /// Whether the model stands in front of the pilot rather than lying below
    /// them — the one question the rest of this class branches on.
    /// </summary>
    private static bool OnTheWall =>
        VrMapConfig.Placement != null && VrMapConfig.Placement.Value == WorldMapPlacement.Wall;

    /// <summary>
    /// Whether the game's own map being open is what is showing this one. Only on
    /// the wall: that is the placement that stands where the flat map stands and
    /// hides it, so the two have to open and close together. The table is
    /// somewhere the pilot chooses to go, and being sent there by a map key is not
    /// the same thing at all.
    /// </summary>
    private static bool TheGameOpenedIt() =>
        OnTheWall &&
        (VrMapConfig.FollowGameMap == null || VrMapConfig.FollowGameMap.Value) &&
        WorldMapGameMap.Maximized;

    private static Transform? Mount()
    {
        var mainCamera = APIBus.MainCamera;
        if (mainCamera == null) return null;
        return mainCamera.transform.parent != null ? mainCamera.transform.parent : mainCamera.transform;
    }
}
