using System;
using HarmonyLib;
using UnityEngine;

namespace NOVR.VrMap;

/// <summary>
/// The game's own map, read rather than replaced: where it is centred and how
/// far it is zoomed in.
///
/// <para><b>Why mirror it instead of reading the keys.</b> "Controlled by the
/// map scroll keys" can be built two ways. Reading the same Rewired actions the
/// game reads — <c>Move Map Horizontal</c>, <c>Move Map Vertical</c>,
/// <c>Zoom View</c> — means re-implementing <c>DynamicMap.MapControls</c> and
/// getting the same answer, and it is only the same answer until one of them
/// changes. Reading where the game's map <i>ended up</i> costs two fields and is
/// right by construction: it picks up the keys, the mouse drag on Pan/Tilt View,
/// the edge clamp, the 5 km lead ahead of the aircraft, and jump-to-map — every
/// way the map can move, including ones added later.</para>
///
/// <para><b>What the two numbers mean.</b> The flat map draws its picture at
/// <c>mapImage.localPosition = (-stationaryOffset - positionOffset) * zoom</c>,
/// so the map point that lands in the middle of the panel is
/// <c>stationaryOffset + positionOffset</c>. Those are in map units, which are
/// global metres times <c>mapDisplayFactor</c> — so dividing by it gives the
/// centre in metres, in the same global frame this mod's model is built in.
/// <c>stationaryOffset</c> is the part that follows the aircraft (and leads it
/// by 5 km) and <c>positionOffset</c> is the part the player has scrolled;
/// their sum is the only thing worth knowing.</para>
///
/// <para><b>Only while maximized.</b> <c>DynamicMap.MapControls</c> runs in the
/// maximized branch of its Update and nowhere else, so the scroll keys move
/// nothing when the map is on the helmet. Asking whether the map is maximized is
/// therefore the same question as asking whether it is being driven.</para>
/// </summary>
internal static class WorldMapGameMap
{
    private static AccessTools.FieldRef<global::DynamicMap, Vector2>? _position;
    private static AccessTools.FieldRef<global::DynamicMap, Vector2>? _stationary;
    private static bool _looked;
    private static bool _complained;

    /// <summary>
    /// True while the game's own map is open full-size — which is both when it
    /// is worth replacing and when its scroll keys do anything.
    /// </summary>
    public static bool Maximized
    {
        get
        {
            try
            {
                return global::DynamicMap.mapMaximized && SceneSingleton<global::DynamicMap>.i != null;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Where the flat map is centred, in map metres, and how far it is zoomed.
    /// False if the map is not up or its fields could not be reached, in which
    /// case the caller falls back to driving itself.
    /// </summary>
    public static bool TryRead(out Vector2 centre, out float zoom)
    {
        centre = Vector2.zero;
        zoom = 1f;

        global::DynamicMap? map;
        try
        {
            if (!global::DynamicMap.mapMaximized) return false;
            map = SceneSingleton<global::DynamicMap>.i;
        }
        catch (Exception)
        {
            return false;
        }

        if (map == null) return false;

        // Map units per metre. Zero before the map has ever been opened, and
        // dividing by it would put the centre at infinity.
        var factor = map.mapDisplayFactor;
        if (Mathf.Abs(factor) < 1e-9f) return false;

        if (!Look()) return false;

        try
        {
            var offset = _position!(map) + _stationary!(map);
            centre = offset / factor;
            zoom = Mathf.Max(0.01f, map.GetZoomLevel());
            return true;
        }
        catch (Exception e)
        {
            Complain(e.Message);
            return false;
        }
    }

    /// <summary>
    /// Find the two private fields, once. A rename upstream is the one failure
    /// this cannot work around, so it says so — loudly, and only the first time.
    /// </summary>
    private static bool Look()
    {
        if (_looked) return _position != null && _stationary != null;
        _looked = true;

        try
        {
            _position = AccessTools.FieldRefAccess<global::DynamicMap, Vector2>("positionOffset");
            _stationary = AccessTools.FieldRefAccess<global::DynamicMap, Vector2>("stationaryOffset");
        }
        catch (Exception e)
        {
            _position = null;
            _stationary = null;
            Complain(e.Message);
            return false;
        }

        return true;
    }

    private static void Complain(string message)
    {
        if (_complained) return;
        _complained = true;
        Debug.LogWarning(
            "[NOVR] World map: could not read where the game's own map is centred " +
            $"(DynamicMap.positionOffset / stationaryOffset) — {message}. The model will be driven " +
            "by the thumbsticks instead of by the map scroll keys.");
    }
}
