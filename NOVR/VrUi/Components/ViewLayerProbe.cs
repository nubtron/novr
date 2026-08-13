using System.Reflection;
using HarmonyLib;
using UnityEngine;
using NOVR.VrUi.Capture;

namespace NOVR.VrUi.Components;

/// <summary>
/// Temporary measurement probe for the view layer. Gated on AutoStartMission
/// (the harness flag) so it never runs for a pilot. One line per second:
/// backend state, designator position in view pixels, marker count and the
/// first marker's position against a prediction through the head eye, and the
/// selected-target arrow state. Ten seconds in, it force-selects the marker
/// whose unit is furthest from the view centre so the edge-pin path can be
/// observed without input.
/// </summary>
public class ViewLayerProbe : MonoBehaviour
{
    private static readonly FieldInfo? MarkersField = AccessTools.Field(typeof(CombatHUD), "markers");
    private static readonly FieldInfo? MarkerTransformField = AccessTools.Field(typeof(HUDUnitMarker), "_transform");
    private static readonly FieldInfo? TargetArrowField = AccessTools.Field(typeof(CombatHUD), "targetArrow");

    private float _nextLog;
    private float _selectAt = -1f;
    private bool _selected;

    private void Update()
    {
        if (!(ModConfiguration.Instance?.AutoStartMission?.Value ?? false)) return;
        if (Time.unscaledTime < _nextLog) return;
        _nextLog = Time.unscaledTime + 1f;

        var combatHud = SceneSingleton<CombatHUD>.i;
        if (combatHud == null || combatHud.aircraft == null)
        {
            Debug.Log("[NOVR-VPROBE] no CombatHUD/aircraft yet");
            return;
        }

        if (_selectAt < 0f) _selectAt = Time.unscaledTime + 10f;

        var designator = combatHud.targetDesignator;
        var designatorText = designator == null
            ? "null"
            : $"pos={ToViewPixels(designator.transform.position):F1} color={designator.color} enabled={designator.enabled}";

        var markers = MarkersField?.GetValue(combatHud) as System.Collections.IList;
        var markerText = "none";
        HUDUnitMarker? probeMarker = null;
        if (markers != null && markers.Count > 0)
        {
            foreach (var entry in markers)
            {
                if (entry is HUDUnitMarker m && m.image != null && m.image.enabled) { probeMarker = m; break; }
            }

            if (probeMarker != null)
            {
                var t = MarkerTransformField?.GetValue(probeMarker) as Transform;
                var predicted = PredictViewPixels(probeMarker.unit);
                markerText = $"n={markers.Count} unit={probeMarker.unit?.unitName} " +
                             $"measured={(t != null ? ToViewPixels(t.position) : Vector2.zero):F1} predicted={predicted:F1}";
            }
            else
            {
                markerText = $"n={markers.Count} none-enabled";
            }
        }

        var arrow = TargetArrowField?.GetValue(combatHud) as UnityEngine.UI.Image;
        var arrowText = arrow == null
            ? "null"
            : $"enabled={arrow.enabled} pos={ToViewPixels(arrow.transform.position):F1}";

        Debug.Log($"[NOVR-VPROBE] active={ViewLayerBackend.IsActive} designator[{designatorText}] " +
                  $"marker[{markerText}] arrow[{arrowText}] headYaw={HeadYaw():F1}");

        if (!_selected && Time.unscaledTime >= _selectAt && markers != null)
        {
            HUDUnitMarker? farthest = null;
            var bestDot = 2f;
            var head = ViewLayerBackend.AcquireProjectionCamera();
            foreach (var entry in markers)
            {
                if (entry is not HUDUnitMarker m || m.unit == null) continue;
                if (head == null) break;
                var dot = Vector3.Dot(
                    (m.unit.transform.position - head.transform.position).normalized,
                    head.transform.forward);
                if (dot < bestDot) { bestDot = dot; farthest = m; }
            }

            if (farthest != null)
            {
                _selected = true;
                combatHud.SelectUnit(farthest.unit);
                Debug.Log($"[NOVR-VPROBE] force-selected {farthest.unit.unitName} viewDot={bestDot:F2}");
            }
        }
    }

    /// <summary>Island world position back to view pixels, for legible logs.</summary>
    private static Vector2 ToViewPixels(Vector3 world)
    {
        var origin = ViewLayerBackend.RemapPixels(Vector3.zero);
        return new Vector2(world.x - origin.x, world.y - origin.y);
    }

    private static Vector2 PredictViewPixels(Unit unit)
    {
        var head = ViewLayerBackend.AcquireProjectionCamera();
        if (head == null || unit == null) return Vector2.zero;
        var px = head.WorldToScreenPoint(unit.transform.position);
        return new Vector2(px.x, px.y);
    }

    private static float HeadYaw()
    {
        var head = ViewLayerBackend.AcquireProjectionCamera();
        return head != null ? head.transform.localEulerAngles.y : -1f;
    }
}
