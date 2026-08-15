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
    private readonly List<string> _inventory = new();

    public WorldMapIcons(Transform room) => _room = room;

    public int Count => _icons.Count;

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
        SetVisible(true);
        _seen.Clear();

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

        foreach (var airbase in Airbases(map))
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
        _inventoried = true;

        _inventory.Clear();
        foreach (var placed in _icons)
        {
            if (placed.Key == null || placed.Value == null) continue;
            var kind = placed.Key is AirbaseMapIcon ? "airbase" : "unit";
            var sprite = placed.Value.sprite != null ? placed.Value.sprite.name : "<none>";
            _inventory.Add($"{placed.Key.name} ({kind}, sprite '{sprite}', " +
                           $"{placed.Value.transform.localScale.x * IconRectSize:F2}m)");
        }

        Debug.Log("[NOVR] World map icons: " + string.Join(", ", _inventory));
        _inventory.Clear();
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
    /// The airbase icons, found by component because the dictionary holding them
    /// is private. Re-found on a slow timer rather than every frame: airbases are
    /// generated once per mission and refreshed when one changes hands.
    /// </summary>
    private AirbaseMapIcon[] Airbases(global::DynamicMap map)
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
        icon.sprite = source.iconImage.sprite;
        icon.color = source.iconImage.color;
        icon.enabled = icon.sprite != null;

        var t = icon.transform;
        t.position = model.TransformPoint(mapPosition);
        t.localScale = Vector3.one * (size / IconRectSize);
        // A world-space canvas faces its own +Z, so +Z points at the head.
        var toHead = head.position - t.position;
        if (toHead.sqrMagnitude > 1e-6f) t.rotation = Quaternion.LookRotation(toHead, Vector3.up);
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
    /// The material that makes a symbol an overlay rather than an object.
    ///
    /// <para>By default a world-space canvas depth-tests like anything else, so a
    /// symbol standing on the far side of a ridge is sawn in half by it and one
    /// at ground level is half-buried. That reads as a solid thing embedded in
    /// the terrain, which is not what a map symbol is: it is an annotation, and
    /// an annotation is never occluded by the thing it annotates. <c>UI/Default</c>
    /// takes its depth test from <c>unity_GUIZTestMode</c>, so forcing that to
    /// Always draws every symbol over the model while leaving it sorted normally
    /// against the other symbols.</para>
    /// </summary>
    private Material? Overlay()
    {
        if (VrMapConfig.IconOverlay != null && !VrMapConfig.IconOverlay.Value) return null;
        if (_overlay != null) return _overlay;

        var shader = Shader.Find("UI/Default");
        if (shader == null)
        {
            Debug.LogWarning("[NOVR] World map: no 'UI/Default' shader, so unit symbols will be " +
                             "cut into the terrain instead of drawn over it.");
            return null;
        }

        _overlay = new Material(shader) { name = "NOVR World Map Icon" };
        _overlay.SetInt("unity_GUIZTestMode", (int)CompareFunction.Always);
        return _overlay;
    }

    private Image Obtain(MapIcon source)
    {
        if (_icons.TryGetValue(source, out var existing) && existing != null) return existing;

        var go = new GameObject("Icon");
        go.transform.SetParent(_container!.transform, false);
        go.layer = (int)LayerHelper.GetVrUiLayer();

        var image = go.AddComponent<Image>();
        image.raycastTarget = false;
        image.preserveAspect = true;
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
