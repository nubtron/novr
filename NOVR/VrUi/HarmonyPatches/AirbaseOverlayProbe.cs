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

    private static bool _reported;
    private static bool _reportedLanding;

    [HarmonyPatch(typeof(global::AirbaseOverlay), "LateUpdate")]
    private static class Probe
    {
        [HarmonyPostfix]
        private static void Postfix(global::AirbaseOverlay __instance)
        {
            var landing = LandingField != null && (bool)LandingField.GetValue(__instance);
            if (_reported && (!landing || _reportedLanding)) return;
            _reported = true;
            if (landing) _reportedLanding = true;

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
