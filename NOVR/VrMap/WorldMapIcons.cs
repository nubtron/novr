using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace NOVR.VrMap;

/// <summary>
/// The units, standing on the model.
///
/// <para><b>Nothing here decides what you are allowed to see.</b> Which units
/// appear, what symbol each gets, what colour it is, and — importantly — *where*
/// it is, all come from the game's own map icon for that unit
/// (<c>DynamicMap.TryGetMapIcon</c>). The rules behind that are real game logic:
/// a faction tracking database, spotted times, radar returns, faction mode, and
/// a last-known-position that keeps showing after a contact is lost.
/// Reimplementing them would mean either showing units the pilot has not earned
/// or hiding ones they have, and both are worse than any amount of duplication
/// saved. So this asks the flat map what it is drawing and draws the same thing
/// in three dimensions.</para>
///
/// <para>Position comes back out of the icon's own transform rather than off the
/// unit: <c>DynamicMap</c> places icons at <c>mapPosition * mapDisplayFactor</c>,
/// so dividing by that factor recovers exactly the map coordinates the flat map
/// is showing — including the stale last-known position of a contact that has
/// gone cold.</para>
///
/// <para>Height is the exception and is taken from the unit itself, because the
/// flat map has no height to give and an icon left at sea level would be buried
/// inside a mountain. For a ground unit that puts the symbol on the ground; for
/// an aircraft it puts it at altitude, which is the thing a flat map cannot do
/// at all. ⚠ For a cold contact this means a current altitude under a stale
/// position — a small amount more than the flat map would tell you.</para>
/// </summary>
internal sealed class WorldMapIcons
{
    private const float IconRectSize = 100f;

    private readonly Transform _room;
    private GameObject? _container;
    private Canvas? _canvas;
    private readonly Dictionary<Unit, Image> _icons = new();
    private readonly List<Unit> _stale = new();

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

        foreach (var unit in UnitRegistry.allUnits)
        {
            if (unit == null) continue;

            if (!global::DynamicMap.TryGetMapIcon(unit, out var mapIcon) ||
                mapIcon == null || mapIcon.iconImage == null ||
                !mapIcon.gameObject.activeInHierarchy)
            {
                Retire(unit);
                continue;
            }

            var onMap = mapIcon.transform.localPosition;
            var mapPosition = new Vector3(
                onMap.x / factor,
                unit.transform.position.y - global::Datum.originPosition.y,
                onMap.y / factor);

            var icon = Obtain(unit);
            icon.sprite = mapIcon.iconImage.sprite;
            icon.color = mapIcon.iconImage.color;
            icon.enabled = icon.sprite != null;

            var t = icon.transform;
            t.position = model.TransformPoint(mapPosition);
            t.localScale = Vector3.one * (iconSize / IconRectSize);
            // A world-space canvas faces its own +Z, so +Z points at the head.
            var toHead = head.position - t.position;
            if (toHead.sqrMagnitude > 1e-6f) t.rotation = Quaternion.LookRotation(toHead, Vector3.up);
        }

        // Units that have gone since last frame: the registry drops them, so
        // they simply stop being visited above.
        _stale.Clear();
        foreach (var known in _icons)
        {
            if (known.Key == null) _stale.Add(known.Key);
        }

        foreach (var gone in _stale) Retire(gone);
        _stale.Clear();
    }

    public void SetVisible(bool visible)
    {
        if (_container != null && _container.activeSelf != visible) _container.SetActive(visible);
    }

    public void Clear()
    {
        _icons.Clear();
        if (_container != null) Object.Destroy(_container);
        _container = null;
        _canvas = null;
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

    private Image Obtain(Unit unit)
    {
        if (_icons.TryGetValue(unit, out var existing) && existing != null) return existing;

        var go = new GameObject("Icon");
        go.transform.SetParent(_container!.transform, false);
        go.layer = (int)LayerHelper.GetVrUiLayer();

        var image = go.AddComponent<Image>();
        image.raycastTarget = false;
        image.preserveAspect = true;
        ((RectTransform)go.transform).sizeDelta = new Vector2(IconRectSize, IconRectSize);

        _icons[unit] = image;
        return image;
    }

    private void Retire(Unit unit)
    {
        if (!_icons.TryGetValue(unit, out var image)) return;
        if (image != null) Object.Destroy(image.gameObject);
        _icons.Remove(unit);
    }
}
