using System.Reflection;
using HarmonyLib;
using UnityEngine;
using NOVR.VrUi.Capture;

namespace NOVR.VrUi.Components;

/// <summary>
/// Temporary ground-truth probe for the view layer. Unlike the first probe
/// (which compared the projection against itself and missed the overlay-room
/// mounting bug), this one computes the expected pixel independently: pure
/// math from the world-posed head camera's transform and the panel frustum,
/// no Camera object involved. If the marker's measured pixel matches, the
/// icon really is on the ray to its unit as seen from the pilot's head.
/// Logs aircraft attitude so agreement can be checked through a turn.
/// </summary>
public class ViewLayerProbe : MonoBehaviour
{
    private static readonly FieldInfo? MarkersField = AccessTools.Field(typeof(CombatHUD), "markers");
    private static readonly FieldInfo? MarkerTransformField = AccessTools.Field(typeof(HUDUnitMarker), "_transform");

    private float _nextLog;

    private void Update()
    {
        if (!(ModConfiguration.Instance?.AutoStartMission?.Value ?? false)) return;
        if (Time.unscaledTime < _nextLog) return;
        _nextLog = Time.unscaledTime + 1f;

        var combatHud = SceneSingleton<CombatHUD>.i;
        var head = APIBus.MainCamera;
        if (combatHud == null || combatHud.aircraft == null || head == null || !ViewLayerBackend.IsActive)
        {
            Debug.Log("[NOVR-VPROBE2] waiting (combatHud/aircraft/head/layer)");
            return;
        }

        var markers = MarkersField?.GetValue(combatHud) as System.Collections.IList;
        HUDUnitMarker? probeMarker = null;
        if (markers != null)
        {
            foreach (var entry in markers)
            {
                if (entry is HUDUnitMarker m && m.image != null && m.image.enabled && !m.selected)
                {
                    probeMarker = m;
                    break;
                }
            }
        }

        if (probeMarker == null)
        {
            Debug.Log("[NOVR-VPROBE2] no enabled marker");
            return;
        }

        var t = MarkerTransformField?.GetValue(probeMarker) as Transform;
        if (t == null) return;

        var measured = ToViewPixels(t.position);

        // Ground truth: the unit's direction from the pilot's head, in the
        // head's own frame, mapped through the panel frustum. No Camera.
        var toUnit = probeMarker.unit.transform.position - head.transform.position;
        var local = Quaternion.Inverse(head.transform.rotation) * toUnit;
        Vector2 expected;
        if (local.z <= 0.01f)
        {
            expected = new Vector2(float.NaN, float.NaN);
        }
        else
        {
            var fov = Mathf.Clamp(CapturedHmd.VisorFieldOfView?.Value ?? 70f, 30f, 110f);
            var pxPerTan = ViewLayerBackend.TexWidth * 0.5f / Mathf.Tan(fov * 0.5f * Mathf.Deg2Rad);
            expected = new Vector2(
                ViewLayerBackend.TexWidth * 0.5f + local.x / local.z * pxPerTan,
                ViewLayerBackend.TexHeight * 0.5f + local.y / local.z * pxPerTan);
        }

        var aircraftEuler = combatHud.aircraft.transform.eulerAngles;
        Debug.Log($"[NOVR-VPROBE2] unit={probeMarker.unit.unitName} measured={measured:F1} " +
                  $"expected={expected:F1} delta={(new Vector2(measured.x, measured.y) - expected).magnitude:F2} " +
                  $"acEuler=({aircraftEuler.x:F1}, {aircraftEuler.y:F1}, {aircraftEuler.z:F1}) " +
                  $"headPos={head.transform.position:F0}");
    }

    private static Vector2 ToViewPixels(Vector3 world)
    {
        var origin = ViewLayerBackend.RemapPixels(Vector3.zero);
        return new Vector2(world.x - origin.x, world.y - origin.y);
    }
}
