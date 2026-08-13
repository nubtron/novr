using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using NOVR.VrUi.Capture;
using NuclearOption.UIStyleSystem;
using UnityEngine;
using UnityEngine.UI;

namespace NOVR.VrUi.HarmonyPatches;

/// <summary>
/// Feeds the base game's screen-space icon code the head instead of the fixed
/// eye, while <see cref="ViewLayerBackend"/> is active — the icons themselves
/// run unmodified.
///
/// The flat game positions every view-referenced element with three inputs:
/// <c>CameraStateManager.mainCamera</c> (the projection),
/// <c>CameraStateManager.i.transform</c> (the behind-you cull and the edge-pin
/// angle test) and <c>Screen.width/height</c> (the screen rectangle). In VR
/// those must become the head eye, the head transform and the virtual screen's
/// 1920x1080 — and nothing else may change, so each input is swapped at its
/// seam:
///
/// - the camera is a public field, so a prefix/finalizer pair swaps it around
///   each update method, exactly like <see cref="HudDesignEyePatches"/> (these
///   swaps nest inside that one: markers project through the head while the
///   weapon-state symbology later in the same LateUpdate keeps the design eye);
/// - the transform reads and screen dimensions go through provider methods via
///   a transpiler; the providers return the vanilla values whenever the layer
///   is inactive, so <c>AirbaseOverlay</c>'s calls into the shared
///   <c>PinToScreenEdge</c> and every non-VR configuration stay bit-identical;
/// - each freshly written screen-pixel position is re-expressed on the island
///   canvas by a postfix (<see cref="ViewLayerBackend.RemapPixels"/>) — a
///   coordinate change, not a repositioning.
///
/// The one piece of game arithmetic reproduced rather than redirected is the
/// missile-state fade pair (designator self-hide near the boresight, velocity
/// vector fade near the designator): both are inline
/// <c>Vector3.Distance(a.position, b.position)</c> calls whose operands now
/// live on different canvases, so a postfix re-runs the game's own formula
/// with the distances those positions stand for. The boresight-state
/// equivalent is left alone (its target-box position is a method local); its
/// designator simply stays visible.
/// </summary>
internal static class ViewLayerPatches
{
    // ---------------------------------------------------------------- providers

    private static readonly FieldInfo? CameraStateManagerInstanceField =
        AccessTools.Field(typeof(SceneSingleton<CameraStateManager>), "i");

    /// <summary>The transform the icon code should measure the view from.</summary>
    public static Transform ViewTransform()
    {
        var eye = ViewLayerBackend.AcquireProjectionCamera();
        if (eye != null) return eye.transform;
        return SceneSingleton<CameraStateManager>.i.transform;
    }

    public static int ScreenWidth() => ViewLayerBackend.IsActive ? ViewLayerBackend.TexWidth : Screen.width;

    public static int ScreenHeight() => ViewLayerBackend.IsActive ? ViewLayerBackend.TexHeight : Screen.height;

    // ---------------------------------------------------------------- camera swap

    private static void Swap(out Camera? previous)
    {
        previous = null;

        var eye = ViewLayerBackend.AcquireProjectionCamera();
        if (eye == null) return;

        var manager = SceneSingleton<CameraStateManager>.i;
        if (manager == null || manager.mainCamera == null) return;

        previous = manager.mainCamera;
        manager.mainCamera = eye;
    }

    private static void Restore(Camera? previous)
    {
        if (previous == null) return;

        var manager = SceneSingleton<CameraStateManager>.i;
        if (manager != null) manager.mainCamera = previous;
    }

    // ---------------------------------------------------------------- transpiler

    /// <summary>
    /// Rewrites, in place: <c>SceneSingleton&lt;CameraStateManager&gt;.i.transform</c>
    /// (a field load followed by get_transform) to <see cref="ViewTransform"/>,
    /// and <c>Screen.width/height</c> to the providers. Anything else is left
    /// untouched. A pair whose second instruction carries a branch label is
    /// skipped rather than risked.
    /// </summary>
    private static IEnumerable<CodeInstruction> RedirectViewReads(IEnumerable<CodeInstruction> instructions)
    {
        var list = new List<CodeInstruction>(instructions);

        var getTransform = AccessTools.PropertyGetter(typeof(Component), nameof(Component.transform));
        var getWidth = AccessTools.PropertyGetter(typeof(Screen), nameof(Screen.width));
        var getHeight = AccessTools.PropertyGetter(typeof(Screen), nameof(Screen.height));

        var viewTransform = AccessTools.Method(typeof(ViewLayerPatches), nameof(ViewTransform));
        var screenWidth = AccessTools.Method(typeof(ViewLayerPatches), nameof(ScreenWidth));
        var screenHeight = AccessTools.Method(typeof(ViewLayerPatches), nameof(ScreenHeight));

        for (var i = 0; i < list.Count; i++)
        {
            var instruction = list[i];

            if (instruction.Calls(getWidth))
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = screenWidth;
                continue;
            }

            if (instruction.Calls(getHeight))
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = screenHeight;
                continue;
            }

            if (CameraStateManagerInstanceField != null &&
                i + 1 < list.Count &&
                instruction.LoadsField(CameraStateManagerInstanceField) &&
                list[i + 1].Calls(getTransform) &&
                list[i + 1].labels.Count == 0)
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = viewTransform;
                list.RemoveAt(i + 1);
            }
        }

        return list;
    }

    // ---------------------------------------------------------------- unit markers

    [HarmonyPatch(typeof(CombatHUD), "UpdateMarkers")]
    private static class CombatHudUpdateMarkersPatch
    {
        [HarmonyPrefix]
        private static void Prefix(out Camera? __state) => Swap(out __state);

        [HarmonyFinalizer]
        private static void Finalizer(Camera? __state) => Restore(__state);

        [HarmonyTranspiler]
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) =>
            RedirectViewReads(instructions);
    }

    [HarmonyPatch(typeof(HUDUnitMarker), nameof(HUDUnitMarker.UpdatePosition))]
    private static class UnitMarkerRemapPatch
    {
        private static readonly FieldInfo? TransformField =
            AccessTools.Field(typeof(HUDUnitMarker), "_transform");

        [HarmonyPrefix]
        private static void Prefix(HUDUnitMarker __instance, out Vector3 __state)
        {
            var transform = TransformField?.GetValue(__instance) as Transform;
            __state = transform != null ? transform.position : Vector3.zero;
        }

        // Remap only when the method actually wrote this call — a culled or
        // hidden marker keeps its already-remapped position, and remapping an
        // island coordinate again would corrupt it. Pixel writes and stale
        // island positions cannot collide: a fresh write differs from the
        // stored value by construction.
        [HarmonyPostfix]
        private static void Postfix(HUDUnitMarker __instance, Vector3 __state)
        {
            if (!ViewLayerBackend.IsActive) return;

            var transform = TransformField?.GetValue(__instance) as Transform;
            if (transform == null) return;

            var position = transform.position;
            if (position == __state) return;

            transform.position = ViewLayerBackend.RemapPixels(position);
        }
    }

    [HarmonyPatch(typeof(HUDFunctions), nameof(HUDFunctions.PinToScreenEdge))]
    private static class PinToScreenEdgePatch
    {
        [HarmonyTranspiler]
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) =>
            RedirectViewReads(instructions);
    }

    [HarmonyPatch(typeof(CombatHUD), "SetTargetArrow")]
    private static class TargetArrowRemapPatch
    {
        private static readonly FieldInfo? TargetArrowField =
            AccessTools.Field(typeof(CombatHUD), "targetArrow");

        [HarmonyPostfix]
        private static void Postfix(CombatHUD __instance, bool enabled, Vector3 position)
        {
            if (!ViewLayerBackend.IsActive || !enabled) return;

            var arrow = TargetArrowField?.GetValue(__instance) as Image;
            if (arrow == null) return;

            arrow.transform.position = ViewLayerBackend.RemapPixels(position);
        }
    }

    // ---------------------------------------------------------------- hit markers

    [HarmonyPatch(typeof(CombatHUD), "UpdateHitMarkers")]
    private static class CombatHudUpdateHitMarkersPatch
    {
        [HarmonyPrefix]
        private static void Prefix(out Camera? __state) => Swap(out __state);

        [HarmonyFinalizer]
        private static void Finalizer(Camera? __state) => Restore(__state);
    }

    [HarmonyPatch]
    private static class HitMarkerPositionPatch
    {
        private static readonly Type? HitMarkerType = AccessTools.Inner(typeof(CombatHUD), "HitMarker");
        private static readonly FieldInfo? MarkerField =
            HitMarkerType != null ? AccessTools.Field(HitMarkerType, "marker") : null;

        private static MethodBase TargetMethod() => AccessTools.Method(HitMarkerType, "Position");

        [HarmonyTranspiler]
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) =>
            RedirectViewReads(instructions);

        [HarmonyPostfix]
        private static void Postfix(object __instance)
        {
            if (!ViewLayerBackend.IsActive) return;

            var marker = MarkerField?.GetValue(__instance) as GameObject;
            if (marker == null || !marker.activeSelf) return;

            marker.transform.position = ViewLayerBackend.RemapPixels(marker.transform.position);
        }
    }

    // ---------------------------------------------------------------- radar warnings

    [HarmonyPatch(typeof(RadarWarning), "Update")]
    private static class RadarWarningUpdatePatch
    {
        [HarmonyPrefix]
        private static void Prefix(out Camera? __state) => Swap(out __state);

        [HarmonyFinalizer]
        private static void Finalizer(Camera? __state) => Restore(__state);
    }

    [HarmonyPatch]
    private static class RadarWarningIconPositionPatch
    {
        private static readonly Type? IconType = AccessTools.Inner(typeof(RadarWarning), "RadarWarningIcon");
        private static readonly FieldInfo? ImageField =
            IconType != null ? AccessTools.Field(IconType, "image") : null;

        private static MethodBase TargetMethod() => AccessTools.Method(IconType, "Position");

        [HarmonyTranspiler]
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) =>
            RedirectViewReads(instructions);

        [HarmonyPostfix]
        private static void Postfix(object __instance)
        {
            if (!ViewLayerBackend.IsActive) return;
            RemapImageField(ImageField, __instance);
        }
    }

    [HarmonyPatch]
    private static class JammingIconPositionPatch
    {
        private static readonly Type? IconType = AccessTools.Inner(typeof(RadarWarning), "JammingIcon");
        private static readonly FieldInfo? ImageField =
            IconType != null ? AccessTools.Field(IconType, "image") : null;

        private static MethodBase TargetMethod() => AccessTools.Method(IconType, "Position");

        [HarmonyTranspiler]
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) =>
            RedirectViewReads(instructions);

        [HarmonyPostfix]
        private static void Postfix(object __instance)
        {
            if (!ViewLayerBackend.IsActive) return;
            RemapImageField(ImageField, __instance);
        }
    }

    private static void RemapImageField(FieldInfo? imageField, object instance)
    {
        var image = imageField?.GetValue(instance) as Image;
        if (image == null) return;

        image.transform.position = ViewLayerBackend.RemapPixels(image.transform.position);
    }

    // ---------------------------------------------------------------- missile notch

    [HarmonyPatch(typeof(ThreatItem), "AlignNotchIndicator")]
    private static class NotchIndicatorPatch
    {
        private static readonly FieldInfo? NotchIndicatorField =
            AccessTools.Field(typeof(ThreatItem), "notchIndicator");

        [HarmonyPrefix]
        private static void Prefix(out Camera? __state) => Swap(out __state);

        [HarmonyFinalizer]
        private static void Finalizer(Camera? __state) => Restore(__state);

        [HarmonyPostfix]
        private static void Postfix(ThreatItem __instance)
        {
            if (!ViewLayerBackend.IsActive) return;

            var notch = NotchIndicatorField?.GetValue(__instance) as GameObject;
            if (notch == null) return;

            notch.transform.position = ViewLayerBackend.RemapPixels(notch.transform.position);
        }
    }

    // ---------------------------------------------------------------- objective pointer

    [HarmonyPatch(typeof(ObjectiveOverlay), nameof(ObjectiveOverlay.UpdateOverlay))]
    private static class ObjectiveOverlayPatch
    {
        private static readonly FieldInfo? PointerField =
            AccessTools.Field(typeof(ObjectiveOverlay), "objectivePointer");

        [HarmonyPrefix]
        private static void Prefix(out Camera? __state) => Swap(out __state);

        [HarmonyFinalizer]
        private static void Finalizer(Camera? __state) => Restore(__state);

        [HarmonyTranspiler]
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) =>
            RedirectViewReads(instructions);

        // The pointer and its no-overlap text target move to the island; the
        // objective dot and size indicator stay on the flight-HUD canvas where
        // the game keeps them, in the pixel space that canvas already uses.
        [HarmonyPostfix]
        private static void Postfix(ObjectiveOverlay __instance)
        {
            if (!ViewLayerBackend.IsActive) return;

            var pointer = PointerField?.GetValue(__instance) as Image;
            if (pointer != null)
            {
                pointer.transform.position = ViewLayerBackend.RemapPixels(pointer.transform.position);
            }

            var noOverlap = __instance.TextNoOverlap;
            if (noOverlap != null)
            {
                var target = ViewLayerBackend.RemapPixels(
                    new Vector3(noOverlap.TargetPosition.x, noOverlap.TargetPosition.y, 0f));
                noOverlap.TargetPosition = new Vector2(target.x, target.y);
            }
        }
    }

    // ---------------------------------------------------------------- missile-state fades

    [HarmonyPatch(typeof(HUDMissileState), nameof(HUDMissileState.UpdateWeaponDisplay))]
    private static class MissileStateFadePatch
    {
        [HarmonyPostfix]
        private static void Postfix()
        {
            if (!ViewLayerBackend.IsActive) return;

            var combatHud = SceneSingleton<CombatHUD>.i;
            var flightHud = SceneSingleton<FlightHud>.i;
            if (combatHud == null || combatHud.targetDesignator == null || flightHud == null) return;

            // Designator self-hide near the boresight: the flat game's distance
            // between the HUD centre and the designator, both in view pixels.
            var nose = ViewLayerBackend.NoseInViewPixels();
            var centre = new Vector2(ViewLayerBackend.TexWidth * 0.5f, ViewLayerBackend.TexHeight * 0.5f);
            var designatorDistance = Vector2.Distance(nose, centre);
            combatHud.targetDesignator.color = Color.Lerp(
                Color.black,
                ThemeManager.Active.ColorTheme.AllClear,
                Mathf.Clamp01(designatorDistance * 0.015f - 0.15f));

            // Velocity vector fade near the designator: the designator's
            // stand-in in the flight HUD's own pixel space is the gaze
            // projected through the design eye — the same mapping the gaze
            // designator used before this layer existed.
            var projection = FlightHudCaptureBackend.ActiveDesignProjection;
            if (projection == null || flightHud.velocityVector == null) return;

            var gaze = NOVRHeadsetData.Rotation * Vector3.forward;
            float vectorDistance;
            if (gaze.z < 0.05f)
            {
                vectorDistance = float.MaxValue;
            }
            else
            {
                var p = projection.Value;
                var ndcX = p.m00 * (gaze.x / gaze.z) - p.m02;
                var ndcY = p.m11 * (gaze.y / gaze.z) - p.m12;
                var gazePixels = new Vector3(
                    (ndcX * 0.5f + 0.5f) * Screen.width,
                    (ndcY * 0.5f + 0.5f) * Screen.height,
                    0f);
                vectorDistance = Vector3.Distance(flightHud.velocityVector.transform.position, gazePixels);
            }

            flightHud.velocityVector.color = ThemeManager.Active.ColorTheme.AllClear
                .WithAlpha(Mathf.Clamp01(vectorDistance * 0.015f - 0.15f));
        }
    }
}
