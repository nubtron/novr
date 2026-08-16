using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace NOVR.VrMap;

/// <summary>
/// Air between you and the far side of the model — a layer of it lying on the
/// map, thickest at sea level and thinning with height, the way the real thing
/// does.
///
/// <para><b>Why the map looks flat without this.</b> Everything that tells you
/// how far away a hill is on a real view is the atmosphere in the way: haze
/// thickening, contrast falling, the distance going blue. Shrinking the theatre
/// shrinks the air with it — 40 km of it becomes 33 metres at 1:1200, and 33
/// metres of air does nothing. Stereo carries the near part of the model and
/// gives up on the far part, so the far part reads as a painted backdrop.</para>
///
/// <para><b>Unity's own fog cannot do it, measured.</b> The game's terrain shader
/// ignores <c>RenderSettings</c> entirely: with the range set to 5 km and to 40 km
/// — an eightfold change that should have been the difference between a white-out
/// and a clear day — the frames came back identical to a tenth of a grey level
/// (111.8,124.1,95.6 against 111.7,124.2,95.7). Shader Graph does not include fog
/// unless the graph asks for it, and this one does not. So the air is geometry,
/// and the only question is what shape.</para>
///
/// <para><b>Why flat layers and not shells around your head.</b> Shells were the
/// first answer and they were wrong twice over. They put a hard step in the haze
/// wherever a shell cut the ground, and because a shell cuts level ground in a
/// circle centred on you, the model came out banded in rings that followed you
/// about — reported from a flight as "seams between fog strengths in circles
/// around me", and they are exactly that. And a shell knows only how far away a
/// thing is, so a mountain peak was left as hazy as the valley beside it, which is
/// the opposite of what air does.</para>
///
/// <para>Horizontal layers fix both. A surface is tinted by every layer above it
/// and by none below, so height comes out for free: valleys sit under the whole
/// stack, peaks poke through the top of it, and the sea — the lowest thing on the
/// map — gets all of it. The steps that remain fall along lines of constant
/// altitude, so over water and flat ground, which is most of a map, there are none
/// at all; on a mountainside they read as layered haze in the valleys, which is a
/// real thing to see rather than an artefact. Distance is put back by a gradient
/// carried on each layer's own vertices, centred under your head, so it is a
/// smooth function of distance everywhere and cannot band either.</para>
///
/// <para><b>What makes it depth-correct is the depth buffer.</b> Ground in front
/// of a layer occludes it and is not tinted by it; ground behind it is. Nothing
/// here knows anything about the terrain, and nothing needs to. The layers live
/// inside the model, on the VR UI layer, so only the model is affected — the real
/// world outside is drawn by a different camera and cannot see them.</para>
///
/// <para>Order within the stack does not matter and none is imposed: every layer
/// is the same colour, and compositing k layers of one colour gives the same
/// result whichever way round they go.</para>
/// </summary>
internal sealed class WorldMapHaze
{
    /// <summary>
    /// How many layers the air is cut into. The tradeoff is contouring against
    /// fill rate: the steps only show where terrain crosses a layer, so the
    /// number that matters is how many of them fall on a mountainside — twelve
    /// over the boundary layer puts one every couple of hundred metres of
    /// altitude, which is finer than the terrain's own shading varies.
    /// </summary>
    private const int Layers = 12;

    /// <summary>
    /// Drawn after the model — its terrain is opaque (2000) and its sea is put
    /// at 2450 so the water is under the air rather than painted over it — and
    /// before the symbols, which are annotations and are not in the air.
    /// </summary>
    private const int Queue = 2500;

    /// <summary>
    /// Where the air thins out to nothing, as a fraction of the ceiling. Real
    /// haze does not stop at a line; an exponential with the scale height at half
    /// the ceiling leaves the top layer at 13% of the bottom one, which is a
    /// gradual enough top edge that peaks emerge rather than pop out.
    /// </summary>
    private const float ScaleHeightFraction = 0.5f;

    /// <summary>
    /// How many cells across each layer is cut into. The distance falloff is
    /// carried on the vertices, so this is how finely it can bend: 64 cells over
    /// an 82 km map is one every 1.3 km, against a haze that takes 40 km to go
    /// from nothing to everything.
    /// </summary>
    private const int Cells = 64;

    private readonly List<Transform> _layers = new();
    private readonly List<Material> _materials = new();
    private GameObject? _root;
    private Mesh? _mesh;
    private Color32[]? _colours;
    private Vector3[]? _vertices;
    private Vector2 _built;
    private bool _reported;

    /// <summary>
    /// Put the air on the model.
    ///
    /// <para>Everything here is in the model's own space, which is map metres:
    /// the layers are children of the model root, so they are panned, spun and
    /// scaled with it and none of that has to be repeated. <paramref name="head"/>
    /// is needed only to know where the gradient's centre is and how high we are
    /// looking down from.</para>
    /// </summary>
    public void Refresh(
        Transform model,
        Transform head,
        Vector2 mapSize,
        float ceilingMetres,
        float rangeMetres,
        Color colour,
        float strength)
    {
        if (!Build(model, mapSize)) return;
        SetVisible(true);

        var headLocal = model.InverseTransformPoint(head.position);
        var range = Mathf.Max(1f, rangeMetres);
        var ceiling = Mathf.Max(1f, ceilingMetres);
        var scaleHeight = ceiling * ScaleHeightFraction;

        // The distance falloff, recentred under the head. It rides on the layer's
        // own vertices — see SetGradient for why it is not a texture.
        SetGradient(new Vector2(headLocal.x, headLocal.z), range);

        // Each layer passes exp(-c*w) of what is behind it, so the stack passes
        // exp(-c * sum(w)); solving that for `strength` at the bottom of the stack
        // is what keeps the setting meaning what it says however many layers there
        // are and however they are weighted.
        var weights = new float[Layers];
        var total = 0f;
        for (var i = 0; i < Layers; i++)
        {
            var altitude = ceiling * (i + 0.5f) / Layers;
            weights[i] = Mathf.Exp(-altitude / scaleHeight);
            total += weights[i];
        }

        var c = total > 0f ? -Mathf.Log(1f - Mathf.Clamp(strength, 0.01f, 0.99f)) / total : 0f;
        var drawn = 0;

        for (var i = 0; i < _layers.Count; i++)
        {
            var t = _layers[i];
            if (t == null) continue;

            var altitude = ceiling * (i + 0.5f) / Layers;

            // A layer above the eye is air we are not looking through. Left in, it
            // would tint the model from the wrong side the moment the map was
            // zoomed in far enough to put your head inside the haze.
            if (altitude >= headLocal.y)
            {
                if (t.gameObject.activeSelf) t.gameObject.SetActive(false);
                continue;
            }

            if (!t.gameObject.activeSelf) t.gameObject.SetActive(true);
            drawn++;
            t.localPosition = new Vector3(0f, altitude, 0f);

            var alpha = 1f - Mathf.Exp(-c * weights[i]);
            var tint = new Color(colour.r, colour.g, colour.b, alpha);
            _materials[i].SetColor("_Color", tint);
            _materials[i].SetColor("_BaseColor", tint);
        }

        if (_reported) return;
        _reported = true;

        // What the geometry has been told to do, at four distances, worked out by
        // the same arithmetic the vertices are filled with. Without this the only
        // thing a frame can say is "the model is blue"; with it, a frame that
        // disagrees says which half is wrong. The first version of this was a
        // texture and the frame came back tinted flat at full strength from the
        // near edge to the far one — right shape, no falloff at all — and there
        // was nothing in the log to tell that from a range set too short.
        var sample = new System.Text.StringBuilder();
        foreach (var fraction in new[] { 0f, 0.25f, 0.5f, 1f })
        {
            var through = 1f;
            for (var i = 0; i < Layers; i++) through *= 1f - (1f - Mathf.Exp(-c * weights[i])) * fraction;
            sample.Append($" {range * fraction / 1000f:F0} km {1f - through:F2};");
        }

        Debug.Log(
            $"[NOVR] World map haze: {drawn} of {Layers} layer(s) drawn, sea level to {ceiling:F0} m " +
            $"with the eye at {headLocal.y:F0} m, colour {colour}, queue {Queue}. Sea-level ground " +
            $"should be lost by:{sample}");
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
        _layers.Clear();
        if (_root != null) Object.Destroy(_root);
        if (_mesh != null) Object.Destroy(_mesh);
        _root = null;
        _mesh = null;
        _colours = null;
        _vertices = null;
        _built = Vector2.zero;
        _reported = false;
    }

    /// <summary>
    /// Recentre the distance falloff on the head: clear directly beneath you,
    /// thickening with distance, full by the edge of the range.
    ///
    /// <para><b>On the vertices rather than in a texture.</b> A texture was the
    /// first version and it produced no falloff whatsoever — the model came back
    /// evenly tinted at full strength from the near edge to the far one, with the
    /// height layering working perfectly around it. Whatever the cause, a stack of
    /// map-sized quads carrying one 128-pixel gradient depends on the sampler
    /// agreeing about wrap, filtering and UVs a long way outside 0..1, and none of
    /// that is worth depending on for a value that is a simple function of
    /// distance. A vertex colour is multiplied in by the same line of the same
    /// shader that already applies the layer's own colour, which is known to work
    /// because the layers are visible at all.</para>
    ///
    /// <para>One mesh for the whole stack, so this is one grid's worth of colours
    /// a frame however many layers there are.</para>
    /// </summary>
    private void SetGradient(Vector2 centre, float range)
    {
        if (_mesh == null || _colours == null || _vertices == null) return;

        for (var i = 0; i < _vertices.Length; i++)
        {
            var dx = _vertices[i].x - centre.x;
            var dz = _vertices[i].z - centre.y;
            var alpha = Mathf.Clamp01(Mathf.Sqrt(dx * dx + dz * dz) / range);
            _colours[i] = new Color32(255, 255, 255, (byte)(alpha * 255f));
        }

        _mesh.colors32 = _colours;
    }

    private bool Build(Transform model, Vector2 mapSize)
    {
        // The model is rebuilt when the detail setting or the mission's map
        // changes, and takes its children with it — so "still ours, still on this
        // model, still the right size" is asked in one place, and everything else
        // starts by clearing up. Destroying a root that has already gone with its
        // parent is what releases the mesh and the texture, which are assets
        // rather than children and would otherwise be left behind.
        if (_root != null && _root.transform.parent == model && _built == mapSize) return true;
        Destroy();

        // Shader.Find in a player build only sees shaders the game itself ships,
        // and URP's own Unlit is not one of them — asked for, and it came back
        // null. Sprites/Default is: the model's sea is already painted with it.
        // It is unlit, alpha blended, writes no depth and tests depth normally,
        // which is the entire specification here.
        var shader = Shader.Find("Sprites/Default")
                     ?? Shader.Find("Universal Render Pipeline/Unlit")
                     ?? Shader.Find("Unlit/Transparent");
        if (shader == null)
        {
            Debug.LogWarning("[NOVR] World map haze: no unlit shader to build the air out of.");
            return false;
        }

        _built = mapSize;
        _mesh = BuildGrid(mapSize);

        _root = new GameObject("NOVR World Map Haze");
        _root.transform.SetParent(model, false);

        for (var i = 0; i < Layers; i++)
        {
            var go = new GameObject($"Layer {i}");
            go.transform.SetParent(_root.transform, false);

            go.AddComponent<MeshFilter>().sharedMesh = _mesh;
            var renderer = go.AddComponent<MeshRenderer>();
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.lightProbeUsage = LightProbeUsage.Off;
            renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;

            var material = new Material(shader) { name = $"NOVR World Map Haze {i}" };
            // The transparent recipe URP's own inspector writes: surface type, the
            // blend pair, no depth write, and the queue. Set by hand because a
            // material made from a shader at runtime starts opaque.
            material.SetFloat("_Surface", 1f);
            material.SetFloat("_Blend", 0f);
            material.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
            material.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
            material.SetFloat("_ZWrite", 0f);
            material.SetFloat("_AlphaClip", 0f);
            material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            material.DisableKeyword("_ALPHATEST_ON");
            material.renderQueue = Queue;
            renderer.sharedMaterial = material;

            _layers.Add(go.transform);
            _materials.Add(material);
        }

        LayerHelper.SetLayerRecursive(_root.transform, LayerHelper.GetVrUiLayer());
        Debug.Log($"[NOVR] World map haze: {Layers} layer(s) of '{shader.name}' over " +
                  $"{mapSize.x:F0} x {mapSize.y:F0} m of map.");
        return true;
    }

    /// <summary>
    /// One horizontal grid the size of the map. It is a grid rather than a quad
    /// only so that the distance falloff has somewhere to live; see
    /// <see cref="SetGradient"/>.
    /// </summary>
    private Mesh BuildGrid(Vector2 mapSize)
    {
        var halfX = mapSize.x * 0.5f;
        var halfZ = mapSize.y * 0.5f;
        var side = Cells + 1;

        _vertices = new Vector3[side * side];
        _colours = new Color32[side * side];
        for (var z = 0; z < side; z++)
        {
            for (var x = 0; x < side; x++)
            {
                _vertices[z * side + x] = new Vector3(
                    Mathf.Lerp(-halfX, halfX, x / (float)Cells),
                    0f,
                    Mathf.Lerp(-halfZ, halfZ, z / (float)Cells));
                _colours[z * side + x] = new Color32(255, 255, 255, 255);
            }
        }

        // One winding, and this matters more than it looks. Sprites/Default has
        // `Cull Off` written into the shader, which no material property can
        // override — so a face is drawn whichever side you are on, and a mesh
        // wound both ways is drawn *twice*. Every layer's air was being crossed
        // twice over: measured, the ground 12 km out came back 0.76 lost against
        // the 0.42 the arithmetic asked for, and the far edge saturated long
        // before the range. The both-ways winding was defensive — "whichever
        // shader we ended up with may or may not cull" — and it was the wrong
        // defence against a shader that culls nothing.
        var triangles = new int[Cells * Cells * 6];
        var t = 0;
        for (var z = 0; z < Cells; z++)
        {
            for (var x = 0; x < Cells; x++)
            {
                var a = z * side + x;
                var b = a + 1;
                var c = a + side;
                var d = c + 1;
                triangles[t++] = a; triangles[t++] = c; triangles[t++] = d;
                triangles[t++] = a; triangles[t++] = d; triangles[t++] = b;
            }
        }

        var mesh = new Mesh { name = "NOVR World Map Haze Layer" };
        // 65 x 65 vertices is well inside 16 bits, but the index count is not the
        // thing that overflows quietly, so it is said rather than assumed.
        mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
        mesh.vertices = _vertices;
        mesh.colors32 = _colours;
        mesh.triangles = triangles;
        mesh.RecalculateBounds();
        return mesh;
    }
}
