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

    /// <summary>
    /// The landing symbology — the runway outline, the glideslope line and its
    /// aim point, the airbase marker and label — is world-referenced in exactly
    /// the same way as everything above, and was the one updater left off this
    /// list.
    ///
    /// <para>Measured mid-approach with the capture running: the field was
    /// being projected through <c>CameraStateManager.mainCamera</c>, which by
    /// then is still the game's original "Main Camera" — hundreds of metres
    /// from the aircraft and pointing somewhere else — while every other symbol
    /// on the panel went through the design eye. All four runway corners and
    /// both ends of the glideslope landed within ten pixels of the centre of a
    /// 2560x1440 screen, and the glideslope's length scale came out
    /// <i>negative</i> (-7.9), which is its aim point projecting behind the
    /// camera. A runway box too small to see and a glideslope drawn inside
    /// out.</para>
    /// </summary>
    [HarmonyPatch(typeof(AirbaseOverlay), "LateUpdate")]
    private static class AirbaseOverlayLateUpdatePatch
    {
        [HarmonyPrefix]
        private static void Prefix(out Camera? __state) => Swap(out __state);

        [HarmonyFinalizer]
        private static void Finalizer(Camera? __state) => Restore(__state);
    }

    /// <summary>
    /// The design eye while a swap is in effect, else null.
    ///
    /// <para>Read by <see cref="ViewLayerPatches"/>. The screen-space helpers
    /// it redirects — <c>HUDFunctions.PinToScreenEdge</c> above all — are
    /// shared, and are called from inside these swaps as well as from the view
    /// layer's own. Inside one of these the right reference is the design eye
    /// and the real screen; the view layer's virtual screen would put the
    /// airbase marker in a third coordinate system belonging to neither.</para>
    /// </summary>
    public static Camera? ActiveDesignEye { get; private set; }

    private static void Swap(out Camera? previous)
    {
        previous = null;

        var designEye = FlightHudCaptureBackend.AcquireDesignEyeForProjection();
        if (designEye == null) return;

        var manager = SceneSingleton<CameraStateManager>.i;
        if (manager == null || manager.mainCamera == null) return;

        previous = manager.mainCamera;
        manager.mainCamera = designEye;
        ActiveDesignEye = designEye;
    }

    private static void Restore(Camera? previous)
    {
        if (previous == null) return;

        ActiveDesignEye = null;
        var manager = SceneSingleton<CameraStateManager>.i;
        if (manager != null) manager.mainCamera = previous;
    }
}
