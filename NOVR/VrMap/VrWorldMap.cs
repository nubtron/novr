using NOVR.VrUi;
using UnityEngine;
using UnityEngine.Rendering;

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
/// overlay with the depth buffer cleared. That is what lets a 41 m landscape sit
/// in a cockpit you are strapped into without the canopy rails cutting through
/// it. It also puts the model behind the HUD panels rather than over them, since
/// the model is opaque geometry and they are transparent.</para>
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
    private static readonly int DatumOriginId = Shader.PropertyToID("_Datum_OriginPosition");
    private static readonly int DatumExtentId = Shader.PropertyToID("_Datum_WorldExtent");

    private WorldMapModel? _model;
    private bool _datumOverridden;
    private bool _reported;
    private GameObject? _marker;

    protected override void OnEnable()
    {
        base.OnEnable();
        RenderPipelineManager.beginCameraRendering += OnBeginCameraRendering;
        RenderPipelineManager.endCameraRendering += OnEndCameraRendering;
    }

    protected override void OnDisable()
    {
        base.OnDisable();
        RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;
        RenderPipelineManager.endCameraRendering -= OnEndCameraRendering;
        RestoreDatum();
    }

    private void OnDestroy()
    {
        RestoreDatum();
        if (_marker != null) Destroy(_marker);
        _marker = null;
        _model?.Destroy();
        _model = null;
    }

    private void Update()
    {
        // Safety net. The override is meant to be undone by the matching
        // endCameraRendering, but a frame that never finishes rendering that
        // camera would otherwise leave the *real* terrain reading its texture
        // through a 41 m window — the whole world one flat colour, with nothing
        // on screen to suggest the map was involved.
        RestoreDatum();

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
        ReportOnce(model);
    }

    /// <summary>
    /// One line, the first frame the model is up, saying where it actually is
    /// and whether anything can see it. "The map opens and there is nothing in
    /// it" has too many possible causes to diagnose from a screenshot: the model
    /// could be somewhere else, on the wrong layer, culled, or drawn and
    /// invisible. These are the numbers that separate those.
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
            $"[NOVR] World map placed: root local={root.localPosition} world={root.position} " +
            $"scale={root.localScale.x:E3} layer={model.Root.layer} active={model.Root.activeInHierarchy} " +
            $"datum={(global::Datum.originPosition)}");
        Debug.Log(
            "[NOVR] World map frames: " +
            $"room={(room == null ? "<none>" : $"{room.position} rot={room.rotation.eulerAngles}")} " +
            $"head={(camera == null ? "<none>" : $"{camera.transform.position} rot={camera.transform.rotation.eulerAngles}")} " +
            $"mount={(mount == null ? "<none>" : $"{mount.position} rot={mount.rotation.eulerAngles}")}");
        Debug.Log(
            $"[NOVR] World map first renderer: " +
            (first == null
                ? "<none>"
                : $"{first.name} bounds={first.bounds} visible={first.isVisible} " +
                  $"enabled={first.enabled} material={first.sharedMaterial?.name} " +
                  $"shader={first.sharedMaterial?.shader?.name}") +
            $"; overlay camera={(camera == null ? "<none>" : $"{camera.name} mask=0x{camera.cullingMask:X} " + $"pos={camera.transform.position} far={camera.farClipPlane}")}");
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

        // Rebuild when the mission moves to a different map — the clones hold
        // the old map's meshes, and the old map's prefab has been destroyed.
        if (_model != null && (_model.Root == null || _model.Settings != settings))
        {
            _model.Destroy();
            _model = null;
        }

        return _model ??= WorldMapModel.Build();
    }

    private void Hide()
    {
        if (_model?.Root != null) _model.Root.SetActive(false);
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

    private void OnBeginCameraRendering(ScriptableRenderContext context, Camera camera)
    {
        if (_model?.Root == null || !_model.Root.activeSelf) return;
        if (!VrMapConfig.RemapTerrainDatum.Value) return;
        if (NOUIManager.I == null || camera != NOUIManager.I.CockpitHudCamera) return;

        // The terrain shader reads its position in the map from the pixel's
        // world position measured against these two globals. Point them at the
        // model — origin at the model's map (0,0), extent scaled with it — and
        // every clone reads the same texel it would have read full size.
        var root = _model.Root.transform;
        var scale = root.localScale.x;
        Shader.SetGlobalVector(DatumOriginId, root.position);
        Shader.SetGlobalVector(DatumExtentId, _model.MapSize * 0.5f * scale);
        _datumOverridden = true;
    }

    private void OnEndCameraRendering(ScriptableRenderContext context, Camera camera)
    {
        if (!_datumOverridden) return;
        if (NOUIManager.I == null || camera != NOUIManager.I.CockpitHudCamera) return;
        RestoreDatum();
    }

    private void RestoreDatum()
    {
        if (!_datumOverridden) return;
        _datumOverridden = false;

        Shader.SetGlobalVector(DatumOriginId, global::Datum.originPosition);

        var levelInfo = NetworkSceneSingleton<LevelInfo>.i;
        var settings = levelInfo != null ? levelInfo.LoadedMapSettings : null;
        if (settings != null) Shader.SetGlobalVector(DatumExtentId, settings.MapSize * 0.5f);
    }
}
