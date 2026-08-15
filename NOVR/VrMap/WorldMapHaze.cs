using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace NOVR.VrMap;

/// <summary>
/// Air between you and the far side of the model, built out of nested shells.
///
/// <para><b>Why the map looks flat.</b> Everything that tells you how far away a
/// hill is on a real view is the atmosphere in the way: haze thickening, contrast
/// falling, the distance going blue. Shrinking the theatre shrinks the air with
/// it — 40 km of it becomes 33 metres at 1:1200, and 33 metres of air does
/// nothing. Stereo carries the near part of the model and gives up on the far
/// part, so the far part reads as a painted backdrop.</para>
///
/// <para><b>The obvious fix does not work here, measured.</b> Unity's own fog is
/// one line of settings and the game's terrain shader ignores it: with the range
/// set to 5 km and to 40 km — an eightfold change that should have been the
/// difference between a white-out and a clear day — the frames came back
/// identical to a tenth of a grey level (111.8,124.1,95.6 against
/// 111.7,124.2,95.7). Shader Graph does not include fog unless the graph asks
/// for it, and this one does not.</para>
///
/// <para><b>So the air is geometry.</b> A set of transparent spheres centred on
/// your head, each a little of the haze colour. Ground nearer than a shell
/// occludes it and is not tinted by it; ground further away is behind it and is.
/// Terrain at the far edge sits behind all of them and fades out completely,
/// terrain underfoot is behind none and stays sharp — which is aerial perspective
/// built from the one thing every renderer agrees on, the depth buffer. No shader
/// ships, nothing global is touched, and because the shells live in the overlay
/// room on the VR UI layer, only the model is affected: the real world outside is
/// drawn by a different camera and cannot see them.</para>
///
/// <para>Order does not matter, so none is imposed: every shell is the same
/// colour, and compositing k layers of one colour gives the same result whichever
/// way round they go. They are queued before the symbols, which are annotations
/// and are not in the air.</para>
/// </summary>
internal sealed class WorldMapHaze
{
    /// <summary>
    /// How many shells the air is cut into. The tradeoff is banding against fill
    /// rate: each is a full-screen transparent layer, and eight is where the steps
    /// stop being visible against terrain that is itself varying.
    /// </summary>
    private const int Shells = 8;

    /// <summary>
    /// Drawn after the terrain and before the symbols. 2500 is where opaque ends,
    /// and the icon canvases sit at 3000.
    /// </summary>
    private const int Queue = 2600;

    private readonly Transform _room;
    private readonly List<Transform> _shells = new();
    private readonly List<Material> _materials = new();
    private GameObject? _root;
    private Mesh? _mesh;
    private bool _reported;

    public WorldMapHaze(Transform room) => _room = room;

    /// <summary>
    /// Put the air where the head is, at the model's scale.
    /// <paramref name="rangeMetres"/> is the distance at which ground is lost
    /// completely, already converted out of map kilometres.
    /// </summary>
    public void Refresh(Transform head, float rangeMetres, Color colour, float strength)
    {
        if (!Build()) return;

        SetVisible(true);

        // Nothing inside the first fifth is touched, so the ground under you stays
        // exactly as sharp as it is now and only the distance changes.
        var near = rangeMetres * 0.2f;
        var far = Mathf.Max(near + 0.01f, rangeMetres);

        // Each shell passes the same fraction, so k shells pass that fraction to
        // the k. Solving for the total at the far edge being `strength` gives the
        // per-shell alpha, which keeps the setting meaning what it says however
        // many shells there are.
        var perShell = 1f - Mathf.Pow(1f - Mathf.Clamp01(strength), 1f / Shells);

        for (var i = 0; i < _shells.Count; i++)
        {
            var t = _shells[i];
            if (t == null) continue;

            // Evenly spaced in distance: the haze then thickens linearly with it,
            // which is what a uniform atmosphere does over these ranges.
            var radius = Mathf.Lerp(near, far, (i + 0.5f) / Shells);
            t.position = head.position;
            t.localScale = Vector3.one * (radius * 2f);

            var material = _materials[i];
            material.SetColor("_BaseColor", new Color(colour.r, colour.g, colour.b, perShell));
            material.SetColor("_Color", new Color(colour.r, colour.g, colour.b, perShell));
        }

        if (_reported) return;
        _reported = true;
        Debug.Log($"[NOVR] World map haze: {Shells} shell(s) from {near:F1} m to {far:F1} m " +
                  $"around the head, {perShell:F3} alpha each for {strength:F2} at the far edge, " +
                  $"colour {colour}, queue {Queue}.");
    }

    public void SetVisible(bool visible)
    {
        if (_root != null && _root.activeSelf != visible) _root.SetActive(visible);
    }

    public void Destroy()
    {
        foreach (var material in _materials)
        {
            if (material != null) Object.Destroy(material);
        }

        _materials.Clear();
        _shells.Clear();
        if (_root != null) Object.Destroy(_root);
        if (_mesh != null) Object.Destroy(_mesh);
        _root = null;
        _mesh = null;
        _reported = false;
    }

    private bool Build()
    {
        if (_root != null) return true;

        // Shader.Find in a player build only sees shaders the game itself ships,
        // and URP's own Unlit is not one of them — asked for, and it came back
        // null. Sprites/Default is: the model's sea is already painted with it.
        // It is unlit, alpha blended, writes no depth and tests depth normally,
        // which is the entire specification here.
        var shader = Shader.Find("Sprites/Default")
                     ?? Shader.Find("Universal Render Pipeline/Unlit")
                     ?? Shader.Find("Unlit/Color");
        if (shader == null)
        {
            Debug.LogWarning("[NOVR] World map haze: no unlit shader to build the air out of.");
            return false;
        }

        Debug.Log($"[NOVR] World map haze: building the air out of '{shader.name}'.");

        // One sphere mesh, borrowed off a primitive and then thrown away with its
        // GameObject and collider — cheaper than owning a mesh generator for a
        // shape Unity already ships.
        var donor = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        _mesh = donor.GetComponent<MeshFilter>().sharedMesh;
        Object.Destroy(donor);
        if (_mesh == null) return false;

        _root = new GameObject("NOVR World Map Haze");
        _root.transform.SetParent(_room, false);

        for (var i = 0; i < Shells; i++)
        {
            var go = new GameObject($"Shell {i}");
            go.transform.SetParent(_root.transform, false);

            go.AddComponent<MeshFilter>().sharedMesh = _mesh;
            var renderer = go.AddComponent<MeshRenderer>();
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.lightProbeUsage = LightProbeUsage.Off;
            renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;

            var material = new Material(shader) { name = $"NOVR World Map Haze {i}" };
            // The transparent recipe URP's own inspector writes: surface type,
            // the blend pair, no depth write, and the queue. Set by hand because
            // a material made from a shader at runtime starts opaque.
            material.SetFloat("_Surface", 1f);
            material.SetFloat("_Blend", 0f);
            material.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
            material.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
            material.SetFloat("_ZWrite", 0f);
            // We are inside every one of these, so the faces that can be seen are
            // the ones pointing away: cull the front instead of the back.
            material.SetFloat("_Cull", (float)CullMode.Front);
            material.SetFloat("_AlphaClip", 0f);
            material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            material.DisableKeyword("_ALPHATEST_ON");
            material.renderQueue = Queue;
            renderer.sharedMaterial = material;

            _shells.Add(go.transform);
            _materials.Add(material);
        }

        LayerHelper.SetLayerRecursive(_root.transform, LayerHelper.GetVrUiLayer());
        return true;
    }
}
