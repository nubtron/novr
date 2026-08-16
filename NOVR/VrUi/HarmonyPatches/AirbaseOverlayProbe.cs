using System;
using System.Reflection;
using System.Text;
using HarmonyLib;
using NOVR.VrUi.Capture;
using UnityEngine;

namespace NOVR.VrUi.HarmonyPatches;

// TEMPORARY. Answers one question the source cannot: where the airbase
// overlay's graphics actually live once the flight HUD is captured and the view
// layer has taken the icon layer away. AirbaseOverlay is wired entirely in the
// scene — no code anywhere names it — so the parent chain is only knowable at
// runtime. Revert once the answer is written down.
internal static class AirbaseOverlayProbe
{
    private static readonly FieldInfo? MarkerField = AccessTools.Field(typeof(global::AirbaseOverlay), "airbaseMarker");
    private static readonly FieldInfo? LabelField = AccessTools.Field(typeof(global::AirbaseOverlay), "airbaseLabel");
    private static readonly FieldInfo? BordersField = AccessTools.Field(typeof(global::AirbaseOverlay), "runwayBorders");
    private static readonly FieldInfo? GlideslopeField = AccessTools.Field(typeof(global::AirbaseOverlay), "glideslope");
    private static readonly FieldInfo? AimPointField = AccessTools.Field(typeof(global::AirbaseOverlay), "glideslopeAimPoint");
    private static readonly FieldInfo? LandingField = AccessTools.Field(typeof(global::AirbaseOverlay), "landing");

    private static readonly FieldInfo? NearestAirbaseField = AccessTools.Field(typeof(global::AirbaseOverlay), "nearestAirbase");
    private static readonly FieldInfo? UsageField = AccessTools.Field(typeof(global::AirbaseOverlay), "runwayUsage");
    private static readonly FieldInfo? TaxiingField = AccessTools.Field(typeof(global::AirbaseOverlay), "taxiingToRunway");
    private static readonly FieldInfo? TakeoffTimeField = AccessTools.Field(typeof(global::AirbaseOverlay), "takeoffTime");

    private static string? _lastState;
    private static float _nextStateLog;

    [HarmonyPatch(typeof(global::AirbaseOverlay), "LateUpdate")]
    private static class Probe
    {
        [HarmonyPostfix]
        private static void Postfix(global::AirbaseOverlay __instance)
        {
            var landing = LandingField != null && (bool)LandingField.GetValue(__instance);

            // Report on every change of the three things that decide what this
            // overlay's numbers mean, not once: the first LateUpdate of a
            // session runs before either capture backend exists, so a
            // report-once probe answers the question for a configuration that
            // is never the one being asked about.
            // The overlay's own view of the world, on a slower tick: its
            // landing decision is made in a 2 s slow update from private state,
            // and reconstructing that decision from outside was not enough —
            // every input measured true while the decision itself stayed false.
            if (!landing && Time.unscaledTime >= _nextStateLog)
            {
                _nextStateLog = Time.unscaledTime + 3f;
                Debug.Log(DescribeDecision(__instance));
            }

            var state = $"{landing}/{ViewLayerBackend.IsActive}/{FlightHudCaptureBackend.IsActive}";
            if (state == _lastState) return;
            _lastState = state;

            try
            {
                Debug.Log(Describe(__instance, landing));
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[NOVR-PROBE] airbase overlay probe failed: {e}");
            }
        }
    }

    private static int _slowUpdateCalls;

    // Is the decision even being made? takeoffTime never moving off 0 says
    // either "never called" or "called, and the HUD's aircraft was null every
    // time" — opposite causes, and only the call site can tell them apart.
    [HarmonyPatch(typeof(global::AirbaseOverlay), "UpdateNearestAirbase")]
    private static class SlowUpdateProbe
    {
        [HarmonyPrefix]
        private static void Prefix()
        {
            if (_slowUpdateCalls++ >= 6) return;
            var combatHud = SceneSingleton<CombatHUD>.i;
            Debug.Log($"[NOVR-PROBE] UpdateNearestAirbase call {_slowUpdateCalls} " +
                      $"at levelTime={Time.timeSinceLevelLoad:0.0} " +
                      $"hudAircraft={(combatHud != null && combatHud.aircraft != null ? combatHud.aircraft.unitName : "<null>")}");
        }
    }

    private static string DescribeDecision(global::AirbaseOverlay overlay)
    {
        try
        {
            var combatHud = SceneSingleton<CombatHUD>.i;
            var aircraft = combatHud != null ? combatHud.aircraft : null;
            var nearest = NearestAirbaseField?.GetValue(overlay) as Airbase;
            var usage = (Airbase.Runway.RunwayUsage?)UsageField?.GetValue(overlay);
            var taxiing = TaxiingField != null && (bool)TaxiingField.GetValue(overlay);
            var takeoffTime = TakeoffTimeField != null ? (float)TakeoffTimeField.GetValue(overlay) : -1f;

            var sb = new StringBuilder("[NOVR-PROBE] landing decision: ");
            sb.Append("levelTime=").Append(Time.timeSinceLevelLoad.ToString("0.0"))
              .Append(" slowUpdateCalls=").Append(_slowUpdateCalls)
              .Append(" hudAircraft=").Append(aircraft != null ? aircraft.unitName : "<null>")
              .Append(" nearestAirbase=").Append(nearest != null ? nearest.name : "<null>")
              .Append(" usage=").Append(usage.HasValue ? usage.Value.GetName() : "<none>")
              .Append(" taxiing=").Append(taxiing)
              .Append(" takeoffTime=").Append(takeoffTime.ToString("0.0"));

            if (aircraft == null) return sb.ToString();

            sb.Append(" hasTakenOff=")
              .Append(aircraft.pilots != null && aircraft.pilots.Length > 0 && aircraft.pilots[0] != null
                  ? aircraft.pilots[0].flightInfo.HasTakenOff.ToString()
                  : "<no pilot>")
              .Append(" radarAlt=").Append(aircraft.radarAlt.ToString("0.0"))
              .Append(" gear=").Append(aircraft.gearDeployed)
              .Append(" vertical=").Append(aircraft.GetAircraftParameters().verticalLanding);

            if (nearest != null)
            {
                sb.Append(" inRange=").Append(FastMath.InRange(
                    nearest.center.position, aircraft.transform.position, nearest.GetRadius() + 5000f))
                  .Append(" (r=").Append(nearest.GetRadius().ToString("0"))
                  .Append(" d=").Append(Vector3.Distance(nearest.center.position, aircraft.transform.position).ToString("0"))
                  .Append(')');
            }

            if (usage.HasValue)
            {
                var runway = usage.Value.Runway;
                sb.Append(" onApproach=").Append(runway.AircraftOnApproach(aircraft, 2500f, excludeBetweenEndpoints: true))
                  .Append(" align=").Append(Mathf.Abs(Vector3.Dot(
                      aircraft.transform.forward, usage.Value.GetDirection().normalized)).ToString("0.00"))
                  .Append(" toStart=").Append(Vector3.Distance(aircraft.transform.position, runway.Start.position).ToString("0"))
                  .Append(" toEnd=").Append(Vector3.Distance(aircraft.transform.position, runway.End.position).ToString("0"));
            }

            return sb.ToString();
        }
        catch (Exception e)
        {
            return $"[NOVR-PROBE] landing decision unavailable: {e.Message}";
        }
    }

    private static string Describe(global::AirbaseOverlay overlay, bool landing)
    {
        var sb = new StringBuilder();
        sb.Append("[NOVR-PROBE] airbase overlay").Append(landing ? " (LANDING)" : " (not landing)").Append('\n');

        var manager = SceneSingleton<CameraStateManager>.i;
        var main = manager != null ? manager.mainCamera : null;
        sb.Append("  mainCamera during LateUpdate = ").Append(main != null ? main.name : "<null>")
          .Append("  fov=").Append(main != null ? main.fieldOfView.ToString("0.0") : "-")
          .Append("  pos=").Append(main != null ? main.transform.position.ToString("F2") : "-").Append('\n');
        sb.Append("  APIBus.MainCamera = ").Append(APIBus.MainCamera != null ? APIBus.MainCamera.name : "<null>")
          .Append("   CockpitHudCamera = ").Append(APIBus.CockpitHudCamera != null ? APIBus.CockpitHudCamera.name : "<null>").Append('\n');
        sb.Append("  ViewLayer active=").Append(ViewLayerBackend.IsActive)
          .Append("  FlightHudCapture active=").Append(FlightHudCaptureBackend.IsActive)
          .Append("  Screen=").Append(Screen.width).Append('x').Append(Screen.height).Append('\n');

        Line(sb, "overlay", overlay.transform);
        Line(sb, "airbaseMarker", (MarkerField?.GetValue(overlay) as Component)?.transform);
        Line(sb, "airbaseLabel", (LabelField?.GetValue(overlay) as Component)?.transform);
        Line(sb, "glideslope", (GlideslopeField?.GetValue(overlay) as Component)?.transform);
        Line(sb, "glideslopeAimPoint", (AimPointField?.GetValue(overlay) as Component)?.transform);

        if (BordersField?.GetValue(overlay) is Array borders)
        {
            for (var i = 0; i < borders.Length; i++)
                Line(sb, $"runwayBorders[{i}]", (borders.GetValue(i) as Component)?.transform);
        }

        var combatHud = SceneSingleton<CombatHUD>.i;
        Line(sb, "combatHud.iconLayer", combatHud != null ? combatHud.iconLayer : null);

        return sb.ToString();
    }

    private static void Line(StringBuilder sb, string label, Transform? t)
    {
        sb.Append("  ").Append(label).Append(": ");
        if (t == null) { sb.Append("<null>\n"); return; }

        var canvas = t.GetComponentInParent<Canvas>(true);
        var root = canvas != null ? canvas.rootCanvas : null;

        sb.Append(Path(t))
          .Append("\n      canvas=").Append(canvas != null ? canvas.name : "<none>")
          .Append(" root=").Append(root != null ? root.name : "<none>")
          .Append(" mode=").Append(root != null ? ((int)root.renderMode).ToString() : "-")
          .Append(" cam=").Append(root != null && root.worldCamera != null ? root.worldCamera.name : "<none>")
          .Append("\n      world=").Append(t.position.ToString("F1"))
          .Append(" local=").Append(t.localPosition.ToString("F1"))
          .Append(" lossyScale=").Append(t.lossyScale.ToString("F3"))
          .Append(" active=").Append(t.gameObject.activeInHierarchy);

        if (t.GetComponent<Behaviour>() is { } b) sb.Append(" enabled=").Append(b.enabled);
        sb.Append('\n');
    }

    private static string Path(Transform t)
    {
        var path = t.name;
        for (var p = t.parent; p != null; p = p.parent) path = p.name + "/" + path;
        return path;
    }
}
