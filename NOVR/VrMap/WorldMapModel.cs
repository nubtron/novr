using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Rendering;

namespace NOVR.VrMap;

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
/// <para>Ground is identified by <em>the shader its material uses</em>, not by
/// name or by path. Names are the game's to change and say nothing about what a
/// thing is; every piece of ground on every map is drawn by one shader, and
/// nothing else in the map uses it. If a game update renames that shader this
/// finds nothing, which is why it says so loudly and lists what it did find.</para>
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

    public GameObject Root { get; }
    public MapSettings Settings { get; }

    /// <summary>Map extent in metres — the model's size before scaling.</summary>
    public Vector2 MapSize => Settings.MapSize;

    private WorldMapModel(GameObject root, MapSettings settings)
    {
        Root = root;
        Settings = settings;
    }

    /// <summary>
    /// Build the model for whatever map is loaded, or return null (having said
    /// why) if there isn't one yet.
    /// </summary>
    public static WorldMapModel? Build()
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

        // Include inactive: a tile switched off by distance culling is still
        // part of the ground, and the model is meant to be the whole map rather
        // than the part of it that happens to be near the aircraft right now.
        foreach (var source in settings.GetComponentsInChildren<MeshRenderer>(true))
        {
            var material = source.sharedMaterial;
            var shader = material != null ? material.shader : null;
            if (shader == null) continue;

            shadersSeen.Add(shader.name);
            if (shader.name != TerrainShaderName) continue;

            var filter = source.GetComponent<MeshFilter>();
            if (filter == null || filter.sharedMesh == null) continue;

            Clone(root.transform, source, filter.sharedMesh, origin);
            cloned++;
        }

        if (cloned == 0)
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

        LayerHelper.SetLayerRecursive(root.transform, LayerHelper.GetVrUiLayer());
        root.SetActive(false);

        Debug.Log(
            $"[NOVR] World map: modelled '{settings.name}' from {cloned} terrain renderer(s), " +
            $"map {settings.MapSize.x:F0} x {settings.MapSize.y:F0} m.");

        return new WorldMapModel(root, settings);
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

    public void Destroy()
    {
        if (Root != null) Object.Destroy(Root);
    }
}
