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
/// painted across each layer, centred under your head — a texture, so it is smooth
/// everywhere and cannot band.</para>
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

    private const int RampSize = 128;

    private readonly List<Transform> _layers = new();
    private readonly List<Material> _materials = new();
    private GameObject? _root;
    private Mesh? _mesh;
    private Texture2D? _ramp;
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

        // The gradient is painted by moving the map under the texture rather than
        // the texture over the map: four UVs a frame, so the centre follows your
        // head exactly and the falloff stays a smooth function of distance instead
        // of a set of steps. Clamped sampling means everything past the range sits
        // on the texture's edge, which is full haze — the right answer, and the
        // reason the layer can be map-sized while the gradient is not.
        SetGradient(mapSize, new Vector2(headLocal.x, headLocal.z), range);

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
        Debug.Log(
            $"[NOVR] World map haze: {drawn} of {Layers} layer(s) drawn, sea level to {ceiling:F0} m " +
            $"with the eye at {headLocal.y:F0} m, {strength:F2} of the ground lost at {range / 1000f:F0} km " +
            $"and nothing lost underfoot, colour {colour}, queue {Queue}.");
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
        if (_ramp != null) Object.Destroy(_ramp);
        _root = null;
        _mesh = null;
        _ramp = null;
        _built = Vector2.zero;
        _reported = false;
    }

    /// <summary>
    /// Slide the gradient so its centre is under the head. The mesh is shared by
    /// every layer, so this is four vertices' worth of UV per frame for the whole
    /// stack.
    /// </summary>
    private void SetGradient(Vector2 mapSize, Vector2 centre, float range)
    {
        if (_mesh == null) return;

        var halfX = mapSize.x * 0.5f;
        var halfZ = mapSize.y * 0.5f;
        var span = range * 2f;

        _mesh.uv = new[]
        {
            Uv(-halfX, -halfZ, centre, span),
            Uv(halfX, -halfZ, centre, span),
            Uv(halfX, halfZ, centre, span),
            Uv(-halfX, halfZ, centre, span),
        };
    }

    private static Vector2 Uv(float x, float z, Vector2 centre, float span) =>
        new((x - centre.x) / span + 0.5f, (z - centre.y) / span + 0.5f);

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
        _mesh = BuildQuad(mapSize);
        _ramp = BuildRamp();

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

            var material = new Material(shader) { name = $"NOVR World Map Haze {i}", mainTexture = _ramp };
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
    /// One horizontal quad the size of the map, wound both ways — whichever
    /// shader we ended up with may or may not cull, and a layer that vanished
    /// when you looked at it from below would be worse than a wasted triangle.
    /// </summary>
    private static Mesh BuildQuad(Vector2 mapSize)
    {
        var halfX = mapSize.x * 0.5f;
        var halfZ = mapSize.y * 0.5f;

        var mesh = new Mesh { name = "NOVR World Map Haze Layer" };
        mesh.vertices = new[]
        {
            new Vector3(-halfX, 0f, -halfZ), new Vector3(halfX, 0f, -halfZ),
            new Vector3(halfX, 0f, halfZ), new Vector3(-halfX, 0f, halfZ),
        };
        mesh.uv = new[] { Vector2.zero, Vector2.right, Vector2.one, Vector2.up };
        // The stock textured shaders multiply by vertex colour, and a mesh without
        // one is not reliably white.
        mesh.colors = new[] { Color.white, Color.white, Color.white, Color.white };
        mesh.triangles = new[] { 0, 1, 2, 0, 2, 3, 2, 1, 0, 3, 2, 0 };
        mesh.RecalculateBounds();
        return mesh;
    }

    /// <summary>
    /// The distance falloff, as a picture: clear at the centre, opaque by the
    /// edge of the range, and clamped so everything beyond it stays opaque.
    ///
    /// <para>Linear in distance rather than exponential. What the eye is given is
    /// the product of the layers, and a product of linear ramps already curves;
    /// making each one exponential as well pulls all the change into the first few
    /// kilometres and leaves the far half of the model uniformly grey.</para>
    /// </summary>
    private static Texture2D BuildRamp()
    {
        var texture = new Texture2D(RampSize, RampSize, TextureFormat.RGBA32, false)
        {
            name = "NOVR World Map Haze Ramp",
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear,
        };

        var pixels = new Color32[RampSize * RampSize];
        var centre = (RampSize - 1) * 0.5f;
        var edge = RampSize * 0.5f;

        for (var y = 0; y < RampSize; y++)
        {
            for (var x = 0; x < RampSize; x++)
            {
                var dx = (x - centre) / edge;
                var dy = (y - centre) / edge;
                var alpha = Mathf.Clamp01(Mathf.Sqrt(dx * dx + dy * dy));
                pixels[y * RampSize + x] = new Color32(255, 255, 255, (byte)(alpha * 255f));
            }
        }

        texture.SetPixels32(pixels);
        texture.Apply();
        return texture;
    }
}
