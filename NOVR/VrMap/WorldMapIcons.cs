using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;

namespace NOVR.VrMap;

/// <summary>
/// The units and airbases, standing on the model.
///
/// <para><b>Nothing here decides what you are allowed to see.</b> Which units
/// appear, what symbol each gets, what colour it is, how big it is and — most
/// importantly — *where* it is, all come from the game's own map icon for that
/// unit (<c>DynamicMap.TryGetMapIcon</c>). The rules behind that are real game
/// logic: a faction tracking database, spotted times, radar returns, faction
/// mode, and a last-known-position that keeps showing after a contact is lost.
/// Reimplementing them would mean either showing units the pilot has not earned
/// or hiding ones they have, and both are worse than any amount of duplication
/// saved. So this asks the flat map what it is drawing and draws the same thing
/// in three dimensions.</para>
///
/// <para><b>Position comes off the image, not the icon object.</b>
/// <c>UnitMapIcon.UpdateIcon</c> writes the map position to
/// <c>iconImage.transform.localPosition</c> — the child — and leaves the icon
/// object itself at the origin. Reading the icon object gives every unit the
/// same place, in the middle of the map. Dividing the image's local position by
/// <c>mapDisplayFactor</c> recovers exactly the map coordinates the flat map is
/// showing, including the stale last-known position of a contact gone cold and
/// the jitter jamming adds to it.</para>
///
/// <para><b>Height comes from the same record as the position.</b> Not from the
/// unit: that would put a live altitude under a stale position and tell you
/// something the flat map does not know. <c>TrackingInfo.GetPosition()</c>
/// returns the whole tracked point, so its y is exactly as fresh — or as old —
/// as the x and z drawn on the flat map. Height is the one thing added here, and
/// it is added because a flat map has none to give: an icon left at sea level is
/// buried inside a mountain, and an aircraft's altitude is the thing a flat map
/// cannot show at all.</para>
///
/// <para><b>Airbases are the exception that has to be fetched separately.</b>
/// They are not units, are not in <c>iconLookup</c>, and their icons live in a
/// private dictionary the flat map only updates while it is open — and only
/// activates while it is *maximized*. So they are found by component under the
/// map, positioned from <c>airbase.center</c> rather than from an icon that may
/// never have been placed, and deliberately not gated on being active: that gate
/// says the small helmet map is not showing them, which is nothing to do with
/// whether the pilot is allowed to know where their own airbases are. On the
/// full map, which is what this is, they show.</para>
/// </summary>
internal sealed class WorldMapIcons
{
    private const float IconRectSize = 100f;

    /// <summary>
    /// What the flat map draws a nominal unit at:
    /// <c>mapInverseScale * 15 * definition.mapIconSize * MapOptions.iconSize</c>.
    /// Dividing by this turns a drawn size into a multiple of "one ordinary
    /// unit", which is what the model's icon size is then measured in.
    /// </summary>
    private const float NominalIconPixels = 15f;

    /// <summary>What the flat map draws an airbase at — <c>mapInverseScale * 50</c>.</summary>
    private const float AirbaseIconPixels = 50f;

    private readonly Transform _room;
    private GameObject? _container;
    private Canvas? _canvas;
    private Material? _overlay;
    private readonly Dictionary<MapIcon, Image> _icons = new();
    private readonly HashSet<MapIcon> _seen = new();
    private readonly List<MapIcon> _stale = new();
    private AirbaseMapIcon[]? _airbases;
    private float _airbasesFound;
    private bool _inventoried;
    private float _firstIcons;
    private readonly List<string> _inventory = new();
    private readonly Dictionary<MapIcon, Vector3> _placedAt = new();
    private readonly Dictionary<Sprite, Sprite?> _masks = new();
    private readonly List<Texture2D> _maskTextures = new();
    private readonly List<Sprite> _maskSprites = new();
    private bool _maskedThisFrame;

    public WorldMapIcons(Transform room) => _room = room;

    public int Count => _icons.Count;

    /// <summary>How many of them are airbases — the ones you spawn from.</summary>
    public int Airbases { get; private set; }

    /// <summary>
    /// A symbol as something that can be pointed at: where it is, how big it is
    /// in real metres, and the game's own icon behind it — which is what any
    /// click has to be handed back to.
    /// </summary>
    internal readonly struct Placed
    {
        public Placed(MapIcon source, Transform transform, float radius)
        {
            Source = source;
            Transform = transform;
            Radius = radius;
        }

        public readonly MapIcon Source;
        public readonly Transform Transform;
        public readonly float Radius;
    }

    /// <summary>Every symbol currently on the model, for the pointer to aim at.</summary>
    internal IEnumerable<Placed> Symbols()
    {
        foreach (var placed in _icons)
        {
            if (placed.Key == null || placed.Value == null || !placed.Value.enabled) continue;
            var t = placed.Value.transform;
            yield return new Placed(placed.Key, t, t.lossyScale.x * IconRectSize * 0.5f);
        }
    }

    /// <summary>
    /// Bring the icon layer up to date for this frame. <paramref name="model"/>
    /// is the model root, whose transform turns map coordinates into places in
    /// the room; <paramref name="head"/> is what the symbols turn to face.
    /// </summary>
    public void Refresh(Transform model, Transform head, float iconSize)
    {
        var map = SceneSingleton<global::DynamicMap>.i;
        var factor = map != null ? map.mapDisplayFactor : 0f;
        if (map == null || Mathf.Approximately(factor, 0f))
        {
            // No map means no opinion about what is visible, and this layer has
            // no opinion of its own. Better an empty model than a wrong one.
            Clear();
            return;
        }

        EnsureContainer();
        // Re-reads the depth setting, so turning it over takes effect on symbols
        // that already exist rather than only on the next one placed.
        Overlay();
        SetVisible(true);
        _seen.Clear();
        _maskedThisFrame = false;

        // The flat map's own zoom, divided back out so a symbol's size on the
        // model does not change when the pilot zooms the flat map.
        var mapScale = map.mapImage != null ? map.mapImage.transform.localScale.x : 1f;
        var hq = map.HQ;

        foreach (var unit in UnitRegistry.allUnits)
        {
            if (unit == null) continue;

            // activeSelf and the image's own enabled flag, not activeInHierarchy:
            // this map hides the helmet's tactical map while it is up, and the
            // icons hang under it, so the whole chain reads inactive for a reason
            // that has nothing to do with whether the pilot can see that unit.
            // These two are the game's decision about this icon specifically.
            if (!global::DynamicMap.TryGetMapIcon(unit, out var mapIcon) ||
                mapIcon == null || mapIcon.iconImage == null ||
                !mapIcon.gameObject.activeSelf || !mapIcon.iconImage.enabled)
            {
                continue;
            }

            var drawn = mapIcon.iconImage.transform.localPosition;
            var mapPosition = new Vector3(
                drawn.x / factor,
                TrackedHeight(unit, hq),
                drawn.y / factor);

            Place(mapIcon, model, head, mapPosition,
                  iconSize * Relative(mapIcon.iconImage, mapScale, NominalIconPixels));
            _seen.Add(mapIcon);
        }

        Airbases = 0;
        foreach (var airbase in AirbaseIcons(map))
        {
            if (airbase == null || airbase.iconImage == null || airbase.airbase == null) continue;
            var centre = airbase.airbase.center;
            if (centre == null) continue;

            // The flat map only recolours these from UpdateMap, which does not
            // run while it is closed, so a captured airbase would keep its old
            // colour on the model. The game's own method, so its rules stand.
            airbase.UpdateColor();

            Place(airbase, model, head, centre.position - global::Datum.originPosition,
                  iconSize * (AirbaseIconPixels / NominalIconPixels));
            _seen.Add(airbase);
            Airbases++;
        }

        // Anything not visited this frame has gone, been hidden, or stopped
        // being something the pilot can see.
        _stale.Clear();
        foreach (var known in _icons)
        {
            // `== null` is Unity's destroyed-object check, so the reference in
            // the dictionary is still a real one to remove by.
            if (known.Key == null || !_seen.Contains(known.Key)) _stale.Add(known.Key!);
        }

        foreach (var gone in _stale) Retire(gone);
        _stale.Clear();

        Inventory();
    }

    /// <summary>
    /// One line, once, naming every symbol on the model with the sprite it is
    /// wearing and how big that came out. Eight icons is a short list, and
    /// "there is a large red slab on the mountain" is not a question a screenshot
    /// can answer: it says nothing about which unit it is, which sprite the game
    /// handed over, or whether the size came from the flat map's ratios or from
    /// the clamp that catches the building branch.
    /// </summary>
    private void Inventory()
    {
        if (_inventoried || _icons.Count == 0) return;

        // Not on the frame the map opens. DynamicMap refreshes a fifth of its
        // icons per frame and only starts once the mission is running, so a
        // symbol read immediately is still sitting wherever its prefab put it —
        // which is how seven units came back sharing one position off the edge of
        // the map. This is the third measurement on this feature taken in the
        // frame something was switched on, and the third to be worthless.
        if (_firstIcons == 0f) _firstIcons = Time.unscaledTime;
        if (Time.unscaledTime - _firstIcons < 2f) return;

        _inventoried = true;

        _inventory.Clear();
        foreach (var placed in _icons)
        {
            if (placed.Key == null || placed.Value == null) continue;
            var kind = placed.Key is AirbaseMapIcon ? "airbase" : "unit";
            var sprite = placed.Value.sprite != null ? placed.Value.sprite.name : "<none>";
            var at = _placedAt.TryGetValue(placed.Key, out var where) ? where : Vector3.zero;
            _inventory.Add($"{placed.Key.name} ({kind}, sprite '{sprite}', " +
                           $"{placed.Value.transform.localScale.x * IconRectSize:F2}m, " +
                           $"map {at.x:F0},{at.y:F0},{at.z:F0}, {Coverage(placed.Value.sprite)})");
        }

        var mount = APIBus.MainCamera != null ? APIBus.MainCamera.transform : null;
        var here = mount != null ? mount.position - global::Datum.originPosition : Vector3.zero;
        Debug.Log($"[NOVR] World map icons (aircraft at map {here.x:F0},{here.y:F0},{here.z:F0}): " +
                  string.Join(", ", _inventory));
        _inventory.Clear();
        _placedAt.Clear();

        // What the symbols are actually being drawn with, which is the half of
        // "why is it opaque" that is not about the sprite.
        var described = new HashSet<string>();
        foreach (var placed in _icons)
        {
            if (placed.Key == null || placed.Value == null || placed.Key.iconImage == null) continue;
            var theirs = placed.Key.iconImage.material;
            var name = theirs != null ? theirs.name : "<none>";
            if (!described.Add(name)) continue;
            Debug.Log($"[NOVR] World map icon material: {placed.Key.name} — flat map draws with " +
                      $"'{name}', {Describe(theirs)}; the model draws with " +
                      $"'{(placed.Value.material != null ? placed.Value.material.name : "<none>")}', " +
                      $"{Describe(placed.Value.material)}.");
        }
    }

    /// <summary>
    /// A material as its shader and the knobs that shader exposes. The question
    /// this answers is whether a blend or a depth test can be set on it at all:
    /// a fixed-function <c>Blend</c> line in the shader is not something a
    /// material can override, and there is no way to tell from the outside except
    /// by asking the shader what properties it has.
    /// </summary>
    private static string Describe(Material? material)
    {
        if (material == null) return "no material";
        var shader = material.shader;
        if (shader == null) return "no shader";

        var properties = new List<string>();
        for (var i = 0; i < shader.GetPropertyCount(); i++)
        {
            properties.Add($"{shader.GetPropertyName(i)}:{shader.GetPropertyType(i)}");
        }

        return $"shader '{shader.name}' queue {material.renderQueue} " +
               $"[{string.Join(" ", properties)}]";
    }

    /// <summary>
    /// How much of a symbol's sprite is actually solid — the question behind "the
    /// parts inside that should be transparent are not".
    ///
    /// <para>A map symbol drawn on a flat map sits on a picture of the ground, so a
    /// filled interior costs nothing and reads fine. On a solid model it hides the
    /// terrain it is annotating, and there is no way to tell by looking whether
    /// that is the sprite being filled or the blend being wrong. So this reads the
    /// sprite back off the GPU — via a blit, because the icon textures are not
    /// import-readable — and reports the split.</para>
    /// </summary>
    private static string Coverage(Sprite? sprite)
    {
        if (sprite == null) return "no sprite: a plain filled rectangle";
        // ReadPixels stalls on the GPU, and a mission with a hundred units would
        // pay for a hundred of them in the frame the map opens. The question it
        // answers is asked once per change, not once per flight.
        if (VrMapConfig.SelfTest == null || !VrMapConfig.SelfTest.Value) return "sprite not read";

        var pixels = Read(sprite, out var width, out var height);
        if (pixels == null) return "sprite unreadable";

        int clear = 0, partial = 0, solid = 0;
        long dark = 0;
        foreach (var pixel in pixels)
        {
            if (pixel.a < 16) clear++;
            else if (pixel.a > 240) solid++;
            else partial++;

            // The number that mattered: opaque *and* black is a texel the sprite
            // means you to see through and only an additive blend obeys.
            if (pixel.a > 240 && Mathf.Max(pixel.r, Mathf.Max(pixel.g, pixel.b)) < 32) dark++;
        }

        var centre = pixels[(height / 2) * width + (width / 2)];
        var total = Mathf.Max(1, pixels.Length);

        // And the sprite itself, on disk, because "it has a black background" is a
        // claim about a picture and every number here is a summary of one.
        Dump(sprite, pixels, width, height);

        return $"sprite {width}x{height} alpha: {clear * 100f / total:F0}% clear, " +
               $"{partial * 100f / total:F0}% partial, {solid * 100f / total:F0}% solid, " +
               $"centre a={centre.a}, opaque-and-black {dark * 100f / total:F0}%";
    }

    /// <summary>
    /// The height to stand a symbol at, from the same record the flat map takes
    /// its position from. <c>UnitMapIcon</c> uses the tracking record whenever
    /// there is a local faction and falls back to the unit itself when there is
    /// not — spectator, or no HQ — and this follows it exactly, so the altitude
    /// carries the same staleness as the position under it.
    /// </summary>
    private static float TrackedHeight(Unit unit, FactionHQ? hq)
    {
        if (hq != null)
        {
            var tracked = hq.GetTrackingData(unit.persistentID);
            if (tracked != null) return tracked.GetPosition().y;
        }

        return unit.GlobalPosition().y;
    }

    /// <summary>
    /// How big this symbol is relative to an ordinary unit's, as the flat map
    /// draws it — so an airbase stays the landmark it is on the flat map instead
    /// of being one more dot among the tanks.
    ///
    /// <para>Clamped, because the game's building branch mixes two conventions:
    /// a small building is sized in inverse-map-scale units like everything else,
    /// but one over 10 m across is drawn at its true footprint in map pixels,
    /// which is not a multiple of anything. The clamp keeps that from producing a
    /// symbol the size of the model.</para>
    /// </summary>
    private static float Relative(Image image, float mapScale, float nominal)
    {
        var drawn = image.transform.localScale.x * mapScale;
        if (drawn <= 0f) return 1f;
        return Mathf.Clamp(drawn / nominal, 0.4f, 4f);
    }

    /// <summary>
    /// The shape of a symbol, as a multiplier on a square. Buildings are drawn by
    /// the flat map at their real footprint — a long hangar is a long rectangle —
    /// and squaring everything off would lose that. Normalised on x so the size
    /// worked out above still means what it says.
    /// </summary>
    private static Vector3 Aspect(Image image)
    {
        var drawn = image.transform.localScale;
        if (drawn.x <= 0f || drawn.y <= 0f) return Vector3.one;
        return new Vector3(1f, Mathf.Clamp(drawn.y / drawn.x, 0.2f, 5f), 1f);
    }

    /// <summary>
    /// The airbase icons, found by component because the dictionary holding them
    /// is private. Re-found on a slow timer rather than every frame: airbases are
    /// generated once per mission and refreshed when one changes hands.
    /// </summary>
    private AirbaseMapIcon[] AirbaseIcons(global::DynamicMap map)
    {
        var options = SceneSingleton<MapOptions>.i;
        if (options != null && !options.showAirbaseIcon) return System.Array.Empty<AirbaseMapIcon>();

        if (_airbases == null || Time.unscaledTime - _airbasesFound > 2f || AnyMissing(_airbases))
        {
            _airbases = map.GetComponentsInChildren<AirbaseMapIcon>(true);
            _airbasesFound = Time.unscaledTime;
        }

        return _airbases;
    }

    private static bool AnyMissing(AirbaseMapIcon[] icons)
    {
        foreach (var icon in icons)
        {
            if (icon == null) return true;
        }

        return false;
    }

    private void Place(MapIcon source, Transform model, Transform head, Vector3 mapPosition, float size)
    {
        var icon = Obtain(source);
        // A null sprite is not a missing icon: a UI Image with no sprite draws a
        // plain filled rectangle, which is exactly how the flat map draws a
        // building — a footprint, not a symbol. Copying the sprite across
        // reproduces that for free, and disabling the image for want of one
        // would drop from the model something the flat map is showing.
        icon.sprite = Mask(source.iconImage.sprite);
        // The colour is the game's; the opacity is not. A map symbol is an
        // annotation, and the ground it annotates has to stay readable through it —
        // which on a flat map comes free, because the sprite sits on a picture,
        // and here has to be paid for.
        var colour = source.iconImage.color;
        colour.a *= VrMapConfig.IconOpacity != null ? VrMapConfig.IconOpacity.Value : 1f;
        icon.color = colour;
        // Only with a sprite. Preserving the aspect of a sprite that is not
        // there divides by its zero size, and one NaN vertex takes the whole
        // canvas batch with it — every symbol on the model disappeared, not
        // just the five without sprites.
        icon.preserveAspect = icon.sprite != null;

        if (!_inventoried) _placedAt[source] = mapPosition;

        var t = icon.transform;
        t.position = model.TransformPoint(mapPosition);
        t.localScale = Aspect(source.iconImage) * (size / IconRectSize);
        // Square to the view, not aimed at the eye.
        //
        // Aiming each symbol at the head and asking for world up as the up hint
        // reads fine at a distance and comes apart underfoot: for a symbol almost
        // directly below you the direction to the head *is* world up, the two
        // vectors the rotation is built from are parallel, and the roll it falls
        // back on is whatever the remaining rounding says. The symbols then fan
        // out radially from the point under your feet and spin as they pass, which
        // is exactly what a degenerate LookRotation looks like from inside.
        //
        // Taking the head's own rotation has no such case anywhere: every symbol
        // lies in the view plane and every one of them is upright to the reader,
        // looking down at the model or across it. A map symbol is meant to be read,
        // and "upright" for something read is upright in the view.
        t.rotation = head.rotation;
    }

    public void SetVisible(bool visible)
    {
        if (_container != null && _container.activeSelf != visible) _container.SetActive(visible);
    }

    public void Clear()
    {
        _icons.Clear();
        _seen.Clear();
        _airbases = null;
        _inventoried = false;
        _firstIcons = 0f;
        _placedAt.Clear();
        foreach (var sprite in _maskSprites) Object.Destroy(sprite);
        foreach (var texture in _maskTextures) Object.Destroy(texture);
        _maskSprites.Clear();
        _maskTextures.Clear();
        _masks.Clear();
        if (_container != null) Object.Destroy(_container);
        if (_overlay != null) Object.Destroy(_overlay);
        _container = null;
        _canvas = null;
        _overlay = null;
    }

    private void EnsureContainer()
    {
        if (_container != null && _canvas != null) return;

        _container = new GameObject("NOVR World Map Icons");
        _container.transform.SetParent(_room, false);

        _canvas = _container.AddComponent<Canvas>();
        _canvas.renderMode = RenderMode.WorldSpace;
        _canvas.worldCamera = NOVR.VrUi.NOUIManager.I != null
            ? NOVR.VrUi.NOUIManager.I.CockpitHudCamera
            : null;

        // The icons carry their own world positions; the canvas rect only has to
        // be non-degenerate.
        var rect = (RectTransform)_container.transform;
        rect.sizeDelta = new Vector2(IconRectSize, IconRectSize);

        LayerHelper.SetLayerRecursive(_container.transform, LayerHelper.GetVrUiLayer());
    }

    /// <summary>
    /// The material that makes a symbol an annotation rather than an object.
    ///
    /// <para><b>Why the sprites need help at all.</b> They are solid where they are
    /// drawn: measured, the airbase's is 78% fully opaque and opaque at its centre
    /// pixel, an aircraft's glyph likewise, and six of the fifteen symbols in a
    /// mission have no sprite whatsoever and fall back to a plain filled
    /// rectangle. On a flat map, over a picture of the ground, that costs nothing.
    /// On a solid model each one is a slab covering the terrain it exists to
    /// annotate.</para>
    ///
    /// <para><b>Not the game's own blend, and that was measured too.</b> Every map
    /// icon in the game is drawn with <c>Text_additive</c> —
    /// <c>Unlit/AdditiveTextShader</c>, UI/Default with the blend line changed —
    /// which never hides what is under it because it brightens it instead. Copying
    /// that looked like the principled answer and it is the wrong one here: the
    /// flat map's background is always the same dark map image, and the model's is
    /// terrain that runs from dark forest to lit snow, with sky behind anything
    /// standing above it. Tried, and an aircraft symbol against the sky came out
    /// as almost nothing — additive over a background already near white has
    /// nowhere left to go. So this alpha-blends and takes the see-through from
    /// opacity instead, which behaves the same over dark ground and light.</para>
    ///
    /// <para><b>Depth, because a world-space canvas depth-tests like anything
    /// else.</b> A symbol standing on the far side of a ridge is sawn in half by
    /// it, and one at ground level is half-buried. Both read as a solid object
    /// embedded in the terrain. <c>UI/Default</c> takes its depth test from
    /// <c>unity_GUIZTestMode</c>, so forcing that to Always draws every symbol
    /// over the model while leaving the symbols sorted normally against each
    /// other.</para>
    /// </summary>
    private Material? Overlay()
    {
        if (_overlay != null)
        {
            Depth(_overlay);
            return _overlay;
        }

        var shader = Shader.Find("UI/Default");
        if (shader == null)
        {
            Debug.LogWarning("[NOVR] World map: no 'UI/Default' shader, so unit symbols will be " +
                             "cut into the terrain instead of drawn over it.");
            return null;
        }

        _overlay = new Material(shader) { name = "NOVR World Map Icon" };
        Depth(_overlay);
        return _overlay;
    }

    /// <summary>
    /// Set the depth test from the setting, every time, on the one shared
    /// material — so the setting can be turned over mid-flight and so the A/B it
    /// exists for changes exactly one thing. Off does not mean "no material":
    /// that would swap the blend as well and compare two changes at once.
    /// </summary>
    private static void Depth(Material material)
    {
        var over = VrMapConfig.IconOverlay == null || VrMapConfig.IconOverlay.Value;
        material.SetInt("unity_GUIZTestMode",
                        (int)(over ? CompareFunction.Always : CompareFunction.LessEqual));
    }

    /// <summary>
    /// The same symbol with its transparency moved from luminance into alpha.
    ///
    /// <para><b>The game's icon sprites do not keep their shape in the alpha
    /// channel.</b> <c>baseIcon</c> is a white disc with a black aircraft and
    /// black runway bars painted on it, fully opaque throughout: the alpha channel
    /// says "all of this is the symbol" and the black is where you are meant to
    /// see the ground. That works because every icon in the game is drawn with
    /// <c>Text_additive</c>, and black adds nothing — the dark parts of the sprite
    /// are the see-through parts, and the alpha channel is barely used. Drawn with
    /// ordinary alpha blending the same sprite is a solid disc with a solid black
    /// aircraft on it, which is exactly the "black background" a flight
    /// reported.</para>
    ///
    /// <para><b>So convert rather than pick a blend.</b> Additive reproduces the
    /// sprite's intent and fails against a bright background — an aircraft symbol
    /// against the sky came out as almost nothing, measured — because it has
    /// nowhere left to go. Moving the same information into alpha keeps the
    /// intent and loses the failure: alpha is taken from the brightest channel,
    /// the colour is divided back up so it stays at full strength, and the result
    /// composites the same way over dark ground and light. The game's own tint
    /// then does what it always did.</para>
    ///
    /// <para>One sprite per frame, because each costs a GPU readback and a mission
    /// can hold twenty distinct symbols. Until its turn comes a symbol wears the
    /// original, so the layer is never empty — it is briefly the old look.</para>
    /// </summary>
    private Sprite? Mask(Sprite? source)
    {
        if (source == null) return null;
        if (_masks.TryGetValue(source, out var cached)) return cached != null ? cached : source;
        if (_maskedThisFrame) return source;
        _maskedThisFrame = true;

        var pixels = Read(source, out var width, out var height);
        if (pixels == null)
        {
            // Remember the failure: a sprite that cannot be read this frame will
            // not become readable later, and retrying costs a readback a frame.
            _masks[source] = null;
            return source;
        }

        for (var i = 0; i < pixels.Length; i++)
        {
            var pixel = pixels[i];
            var brightest = Mathf.Max(pixel.r, Mathf.Max(pixel.g, pixel.b));
            if (brightest == 0)
            {
                pixels[i] = new Color32(255, 255, 255, 0);
                continue;
            }

            var scale = 255f / brightest;
            pixels[i] = new Color32(
                (byte)Mathf.Min(255f, pixel.r * scale),
                (byte)Mathf.Min(255f, pixel.g * scale),
                (byte)Mathf.Min(255f, pixel.b * scale),
                (byte)(pixel.a * brightest / 255));
        }

        var texture = new Texture2D(width, height, TextureFormat.RGBA32, false)
        {
            name = "NOVR " + source.name,
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp
        };
        texture.SetPixels32(pixels);
        texture.Apply(false);

        var size = source.rect.size;
        var pivot = size.x > 0f && size.y > 0f
            ? new Vector2(source.pivot.x / size.x, source.pivot.y / size.y)
            : new Vector2(0.5f, 0.5f);

        var masked = Sprite.Create(texture, new Rect(0f, 0f, width, height), pivot,
                                   source.pixelsPerUnit > 0f ? source.pixelsPerUnit : 100f);
        masked.name = "NOVR " + source.name;

        _maskTextures.Add(texture);
        _maskSprites.Add(masked);
        _masks[source] = masked;
        return masked;
    }

    /// <summary>
    /// A sprite's pixels, through a blit, because the icon textures are not
    /// import-readable and <c>GetPixels32</c> on them throws.
    /// </summary>
    private static Color32[]? Read(Sprite sprite, out int width, out int height)
    {
        width = 0;
        height = 0;

        var texture = sprite.texture;
        if (texture == null) return null;

        var rect = sprite.textureRect;
        width = Mathf.Clamp(Mathf.RoundToInt(rect.width), 1, 1024);
        height = Mathf.Clamp(Mathf.RoundToInt(rect.height), 1, 1024);

        RenderTexture? target = null;
        Texture2D? readable = null;
        var previous = RenderTexture.active;
        try
        {
            target = RenderTexture.GetTemporary(
                texture.width, texture.height, 0,
                RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
            Graphics.Blit(texture, target);
            RenderTexture.active = target;

            readable = new Texture2D(width, height, TextureFormat.RGBA32, false);
            readable.ReadPixels(new Rect(rect.x, rect.y, width, height), 0, 0);
            readable.Apply(false);
            return readable.GetPixels32();
        }
        catch (System.Exception error)
        {
            Debug.LogWarning($"[NOVR] World map: could not read sprite '{sprite.name}': {error.Message}");
            return null;
        }
        finally
        {
            RenderTexture.active = previous;
            if (target != null) RenderTexture.ReleaseTemporary(target);
            if (readable != null) Object.Destroy(readable);
        }
    }

    /// <summary>
    /// Write a sprite out beside the frame dumps, RGBA as the game holds it, so
    /// the sprite and the pixels it produced on screen can be put side by side.
    /// </summary>
    private static void Dump(Sprite sprite, Color32[] pixels, int width, int height)
    {
        Texture2D? image = null;
        try
        {
            var folder = System.IO.Path.Combine(
                BepInEx.Paths.PluginPath, "NOVR", "dumps", "sprites");
            System.IO.Directory.CreateDirectory(folder);
            var safe = sprite.name;
            foreach (var bad in System.IO.Path.GetInvalidFileNameChars()) safe = safe.Replace(bad, '_');

            image = new Texture2D(width, height, TextureFormat.RGBA32, false);
            image.SetPixels32(pixels);
            image.Apply(false);

            var path = System.IO.Path.Combine(folder, safe + ".png");
            System.IO.File.WriteAllBytes(path, image.EncodeToPNG());
            Debug.Log($"[NOVR] World map: wrote sprite '{sprite.name}' to {path}.");
        }
        catch (System.Exception error)
        {
            Debug.LogWarning($"[NOVR] World map: could not write sprite '{sprite.name}': {error.Message}");
        }
        finally
        {
            if (image != null) Object.Destroy(image);
        }
    }

    private Image Obtain(MapIcon source)
    {
        if (_icons.TryGetValue(source, out var existing) && existing != null) return existing;

        var go = new GameObject("Icon");
        go.transform.SetParent(_container!.transform, false);
        go.layer = (int)LayerHelper.GetVrUiLayer();

        var image = go.AddComponent<Image>();
        image.raycastTarget = false;
        var overlay = Overlay();
        if (overlay != null) image.material = overlay;
        ((RectTransform)go.transform).sizeDelta = new Vector2(IconRectSize, IconRectSize);

        _icons[source] = image;
        return image;
    }

    private void Retire(MapIcon source)
    {
        if (!_icons.TryGetValue(source, out var image)) return;
        if (image != null) Object.Destroy(image.gameObject);
        _icons.Remove(source);
    }
}
