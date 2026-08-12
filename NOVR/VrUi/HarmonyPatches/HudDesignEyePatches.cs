using HarmonyLib;
using NOVR.VrUi.Capture;
using UnityEngine;

namespace NOVR.VrUi.HarmonyPatches;

/// <summary>
/// While the game's HUD code runs, make it project from the fixed design eye
/// instead of the head — the same separation a real HUD has between the
/// symbol generator (fixed design eye) and the pilot's actual eye (handled by
/// the collimator; here, by the panel's distance).
///
/// Every world-referenced HUD symbol goes through the public field
/// <c>CameraStateManager.mainCamera</c> — <c>FlightHud.Update</c> (boresight,
/// velocity vector, ladder roll and scale), <c>CombatHUD.LateUpdate</c> (unit
/// markers via <c>HUDUnitMarker.UpdatePosition</c>, hit markers, edge pinning
/// via <c>HUDFunctions.PinToScreenEdge</c>, weapon displays) and
/// <c>CombatHUD.FixedUpdate</c> (weapon state). Because it is a plain field,
/// the patch is a swap, not a transpiler: the prefix stores the head camera
/// and writes the design eye in; a finalizer (which runs even if the original
/// throws) puts the head camera back. Nothing outside these calls ever sees
/// the swapped field.
///
/// The swap is inert unless <see cref="FlightHudCaptureBackend"/> is actively
/// capturing with conformal mode on — <c>ConformalProjectionCamera</c> is null
/// in every other state, including the whole life of a session with the
/// captured HUD disabled.
///
/// Known approximation: the behind-the-camera checks in <c>CombatHUD</c> and
/// <c>HUDUnitMarker</c> use <c>CameraStateManager.i.transform.forward</c> —
/// the head, not the field — so a marker can be culled by where the pilot
/// looks rather than by the airframe frustum. Wrong only past 90 degrees off
/// boresight, where the panel is out of view anyway.
/// </summary>
internal static class HudDesignEyePatches
{
    [HarmonyPatch(typeof(FlightHud), "Update")]
    private static class FlightHudUpdatePatch
    {
        [HarmonyPrefix]
        private static void Prefix(out Camera? __state) => Swap(out __state);

        [HarmonyFinalizer]
        private static void Finalizer(Camera? __state) => Restore(__state);
    }

    [HarmonyPatch(typeof(CombatHUD), "LateUpdate")]
    private static class CombatHudLateUpdatePatch
    {
        [HarmonyPrefix]
        private static void Prefix(out Camera? __state) => Swap(out __state);

        [HarmonyFinalizer]
        private static void Finalizer(Camera? __state) => Restore(__state);
    }

    [HarmonyPatch(typeof(CombatHUD), "FixedUpdate")]
    private static class CombatHudFixedUpdatePatch
    {
        [HarmonyPrefix]
        private static void Prefix(out Camera? __state) => Swap(out __state);

        [HarmonyFinalizer]
        private static void Finalizer(Camera? __state) => Restore(__state);
    }

    private static void Swap(out Camera? previous)
    {
        previous = null;

        var designEye = FlightHudCaptureBackend.ConformalProjectionCamera;
        if (designEye == null) return;

        var manager = SceneSingleton<CameraStateManager>.i;
        if (manager == null || manager.mainCamera == null) return;

        previous = manager.mainCamera;
        manager.mainCamera = designEye;
    }

    private static void Restore(Camera? previous)
    {
        if (previous == null) return;

        var manager = SceneSingleton<CameraStateManager>.i;
        if (manager != null) manager.mainCamera = previous;
    }
}
