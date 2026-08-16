using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;

namespace NOVR.VrMap;

/// <summary>
/// How much of the map's own geometry the model is made of.
/// </summary>
public enum WorldMapDetail
{
    /// <summary>
    /// The ground tiles alone — every renderer drawn by the terrain shader.
    /// Cheapest, and the map has holes in it: roads, city surfaces and fields
    /// are separate meshes filling cut-outs in the tiles, so leaving them out
    /// leaves you able to see through the map where they are.
    /// </summary>
    Terrain,

    /// <summary>
    /// Everything laid on the ground — tiles, roads, city surfaces, fields —
    /// but nothing standing on it. Buildings are excluded by what they are
    /// (<c>MapBuilding</c>, or a <c>Unit</c>), not by where they sit.
    /// </summary>
    Surfaces,

    /// <summary>
    /// The whole map, buildings and all. Thousands of renderers; the model
    /// stops being cheap.
    /// </summary>
    Everything,
}

/// <summary>
/// A miniature of the whole map, built by cloning the game's own terrain
/// renderers into one root and shrinking that root.
///
/// <para>Nothing is generated: the clones share the game's meshes and its
/// materials, so the model is the actual ground, at the actual art, and it costs
/// no memory beyond a few hundred GameObjects. The map is instantiated as a
/// single prefab (<c>MapSettingsManager.LoadMap</c>) rather than streamed, so
/// every tile is already resident whenever a mission is loaded — there is no
/// point at which half the world is missing.</para>
///
/// <para>Nothing here is found by name. Ground tiles are identified by <em>the
/// shader their material uses</em> — every piece of ground on every map is drawn
/// by one shader and nothing else uses it — and buildings are excluded by <em>the
/// components they carry</em> (<c>MapBuilding</c>, or a <c>Unit</c>). Names are
/// the game's to change and say nothing about what a thing is; the grouping in
/// the prefab is worse still, because the node called <c>terrain2_roads</c> holds
/// roughly 98 roads and about 1150 city blocks. If a game update renames the
/// shader this finds nothing, which is why it says so loudly and lists what it
/// did find.</para>
///
/// <para>Only the tiles carry that shader — 256 of roughly 2700 renderers under
/// the map. Roads, city surfaces and fields are separate meshes that <em>fill
/// cut-outs in the tiles</em>, so a model made of tiles alone has holes you can
/// see through wherever asphalt is. That is what <see cref="WorldMapDetail"/>
/// is for.</para>
///
/// <para>The clones are placed in map coordinates — world position minus the
/// floating origin — so the model is self-contained the moment it is built. The
/// origin shifts as you fly; the model does not care, because it is no longer
/// expressed in terms of it.</para>
/// </summary>
internal sealed class WorldMapModel
{
    /// <summary>
    /// The one shader every map's ground is drawn by, and nothing else is.
    /// Checked against <c>Shader.name</c>, which is the asset's own name.
    /// </summary>
    private const string TerrainShaderName = "Shader Graphs/TerrainShader";

    /// <summary>
    /// Where the sea is drawn: after the opaque terrain, before the air.
    /// See <see cref="WorldMapHaze"/> for the rest of the model's order.
    /// </summary>
    private const int SeaQueue = 2450;

    public GameObject Root { get; }
    public MapSettings Settings { get; }
    public WorldMapDetail Detail { get; }

    /// <summary>Whether this model was built with a sea, so a change rebuilds it.</summary>
    public bool HasSea { get; }

    // Ours to destroy: the renderer's material is an instance, and the quad is
    // generated. Destroying the GameObject alone leaks both.
    private readonly Material? _seaMaterial;
    private readonly Mesh? _seaMesh;

    /// <summary>Map extent in metres — the model's size before scaling.</summary>
    public Vector2 MapSize => Settings.MapSize;

    private WorldMapModel(
        GameObject root, MapSettings settings, WorldMapDetail detail, Material? seaMaterial, Mesh? seaMesh)
    {
        Root = root;
        Settings = settings;
        Detail = detail;
        HasSea = seaMaterial != null;
        _seaMaterial = seaMaterial;
        _seaMesh = seaMesh;
    }

    /// <summary>
    /// Build the model for whatever map is loaded, or return null (having said
    /// why) if there isn't one yet.
    /// </summary>
    public static WorldMapModel? Build(WorldMapDetail detail)
    {
        var levelInfo = NetworkSceneSingleton<LevelInfo>.i;
        var settings = levelInfo != null ? levelInfo.LoadedMapSettings : null;
        if (settings == null)
        {
            Debug.LogWarning("[NOVR] World map: no map is loaded, nothing to model.");
            return null;
        }

        if (global::Datum.origin == null)
        {
            Debug.LogWarning("[NOVR] World map: the floating origin is not set up yet.");
            return null;
        }

        var origin = global::Datum.originPosition;
        var root = new GameObject("NOVR World Map");
        var shadersSeen = new HashSet<string>();
        var cloned = 0;
        var ground = 0;
        var skippedBuildings = 0;

        // Include inactive: a tile switched off by distance culling is still
        // part of the ground, and the model is meant to be the whole map rather
        // than the part of it that happens to be near the aircraft right now.
        foreach (var source in settings.GetComponentsInChildren<MeshRenderer>(true))
        {
            var material = source.sharedMaterial;
            var shader = material != null ? material.shader : null;
            if (shader == null) continue;

            shadersSeen.Add(shader.name);
            var isGround = shader.name == TerrainShaderName;
            if (isGround) ground++;

            if (!Wanted(source, detail, isGround, ref skippedBuildings)) continue;

            var filter = source.GetComponent<MeshFilter>();
            if (filter == null || filter.sharedMesh == null) continue;

            Clone(root.transform, source, filter.sharedMesh, origin);
            cloned++;
        }

        if (ground == 0)
        {
            // Loud on purpose. The failure this guards against is a game update
            // renaming the shader, and its symptom without this line is "the map
            // opens and there is nothing in it" — indistinguishable from a dozen
            // other causes. The list is what makes the fix a one-line change.
            Debug.LogError(
                $"[NOVR] World map: found no renderer using '{TerrainShaderName}' under the loaded map " +
                $"'{settings.name}'. The terrain shader has probably been renamed. Shaders present: " +
                string.Join(", ", shadersSeen.OrderBy(s => s).Take(40)));
            Object.Destroy(root);
            return null;
        }

        Material? seaMaterial = null;
        Mesh? seaMesh = null;
        if (VrMapConfig.Sea == null || VrMapConfig.Sea.Value)
            AddSea(root.transform, settings, out seaMaterial, out seaMesh);

        LayerHelper.SetLayerRecursive(root.transform, LayerHelper.GetVrUiLayer());
        root.SetActive(false);

        Debug.Log(
            $"[NOVR] World map: modelled '{settings.name}' at detail {detail} from {cloned} renderer(s) " +
            $"({ground} ground, {skippedBuildings} building(s) left out), " +
            $"map {settings.MapSize.x:F0} x {settings.MapSize.y:F0} m.");

        return new WorldMapModel(root, settings, detail, seaMaterial, seaMesh);
    }

    /// <summary>
    /// Whether this renderer belongs in the model at the chosen detail.
    ///
    /// <para>A building is anything carrying a <c>MapBuilding</c> (the city
    /// blocks and props laid out with the map) or a <c>Unit</c> (airbase
    /// structures, which are network objects). Both are asked for up the parent
    /// chain, because a building is a small hierarchy and the renderers are its
    /// leaves.</para>
    /// </summary>
    private static bool Wanted(MeshRenderer source, WorldMapDetail detail, bool isGround, ref int skippedBuildings)
    {
        switch (detail)
        {
            case WorldMapDetail.Terrain:
                return isGround;

            case WorldMapDetail.Everything:
                return true;

            default:
                if (isGround) return true;
                if (source.GetComponentInParent<MapBuilding>(true) == null &&
                    source.GetComponentInParent<Unit>(true) == null) return true;
                skippedBuildings++;
                return false;
        }
    }

    /// <summary>
    /// Copy one renderer into the model. Meshes and materials are shared, not
    /// duplicated — this is the same ground, drawn a second time.
    /// </summary>
    private static void Clone(Transform parent, MeshRenderer source, Mesh mesh, Vector3 origin)
    {
        var go = new GameObject(source.gameObject.name);
        go.transform.SetParent(parent, false);

        source.transform.GetPositionAndRotation(out var position, out var rotation);
        go.transform.localPosition = position - origin;
        go.transform.localRotation = rotation;
        go.transform.localScale = source.transform.lossyScale;

        go.AddComponent<MeshFilter>().sharedMesh = mesh;

        var renderer = go.AddComponent<MeshRenderer>();
        renderer.sharedMaterials = source.sharedMaterials;

        // The model is scenery, not part of the lit world: it must not cast into
        // the real scene's shadow map, and at 1:2000 a shadow would be noise
        // anyway. Probes are off for the same reason — the model is metres from
        // the cockpit and would otherwise be lit by whatever probe the aircraft
        // is sitting in.
        renderer.shadowCastingMode = ShadowCastingMode.Off;
        renderer.receiveShadows = false;
        renderer.lightProbeUsage = LightProbeUsage.Off;
        renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;

        // The terrain is lightmapped, and the lightmap is per-renderer state
        // rather than something the material carries. Without these two the
        // model comes out unlit-flat next to ground that is baked.
        renderer.lightmapIndex = source.lightmapIndex;
        renderer.lightmapScaleOffset = source.lightmapScaleOffset;
    }

    /// <summary>
    /// One quad of sea under the whole model.
    ///
    /// <para>The game's water is not part of the map prefab and cannot be
    /// cloned: it is a single plane owned by <c>LevelInfo</c> that is moved to
    /// sit under the camera every frame, with its shader reading the ocean
    /// textures by world position. Nothing about that survives being shrunk. So
    /// the model gets its own sea — the map's own <c>OceanBasecolor</c>, which
    /// is a picture of the whole map's water, stretched once across it. Without
    /// this the model has no coastline: the ground simply stops and you see
    /// whatever is behind the model through the gap.</para>
    ///
    /// <para>Drawn in the transparent queue with no depth write, so terrain in
    /// front of it hides it exactly as it should and it fills only the water.</para>
    /// </summary>
    private static void AddSea(Transform root, MapSettings settings, out Material? material, out Mesh? mesh)
    {
        material = null;
        mesh = null;

        var texture = settings.OceanBasecolor;
        if (texture == null)
        {
            Debug.LogWarning($"[NOVR] World map: '{settings.name}' has no ocean texture; the model gets no sea.");
            return;
        }

        var shader = FindShader("Sprites/Default", "UI/Default", "Unlit/Texture", "Universal Render Pipeline/Unlit");
        if (shader == null)
        {
            Debug.LogError("[NOVR] World map: found none of the stock textured shaders; the model gets no sea.");
            return;
        }

        var halfX = settings.MapSize.x * 0.5f;
        var halfZ = settings.MapSize.y * 0.5f;

        mesh = new Mesh { name = "NOVR World Map Sea" };
        mesh.vertices = new[]
        {
            new Vector3(-halfX, 0f, -halfZ), new Vector3(halfX, 0f, -halfZ),
            new Vector3(halfX, 0f, halfZ), new Vector3(-halfX, 0f, halfZ),
        };
        mesh.uv = new[] { new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(0f, 1f) };
        // White, because the stock textured shaders multiply by vertex colour
        // and a mesh without one is not reliably white.
        mesh.colors = new[] { Color.white, Color.white, Color.white, Color.white };
        // Both windings: whichever shader we ended up with may or may not cull.
        mesh.triangles = new[] { 0, 1, 2, 0, 2, 3, 2, 1, 0, 3, 2, 0 };
        mesh.RecalculateBounds();

        var go = new GameObject("Sea");
        go.transform.SetParent(root, false);
        go.AddComponent<MeshFilter>().sharedMesh = mesh;

        var renderer = go.AddComponent<MeshRenderer>();
        material = new Material(shader) { mainTexture = texture };
        // Before the haze, which is at 2500, and after the terrain. The stock
        // sprite shader comes out of the box at 3000 — the transparent queue —
        // which drew the sea *over* the air and left every stretch of water on
        // the model perfectly clear while the land beside it faded. Reported from
        // a flight as the haze "not handling the water correctly", and it was
        // exactly this: the ocean is the largest and flattest thing on the map, so
        // an unhazed sea is most of what you see at the far end of it.
        material.renderQueue = SeaQueue;
        renderer.material = material;
        renderer.shadowCastingMode = ShadowCastingMode.Off;
        renderer.receiveShadows = false;
        renderer.lightProbeUsage = LightProbeUsage.Off;
        renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;

        Debug.Log(
            $"[NOVR] World map: sea from '{texture.name}' ({texture.width}x{texture.height}) " +
            $"on shader '{shader.name}'.");
    }

    private static Shader? FindShader(params string[] names)
    {
        foreach (var name in names)
        {
            var shader = Shader.Find(name);
            if (shader != null) return shader;
        }

        return null;
    }

    public void Destroy()
    {
        if (Root != null) Object.Destroy(Root);
        if (_seaMaterial != null) Object.Destroy(_seaMaterial);
        if (_seaMesh != null) Object.Destroy(_seaMesh);
    }
}
