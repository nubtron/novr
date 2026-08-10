using System.Collections.Generic;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;
using NOVR.VrUi.SpecialBehavior;

namespace NOVR.VrUi.HarmonyPatches;

/// <summary>
/// Restores hierarchy draw order inside the game's own menus once they have
/// been converted to world space.
///
/// A ScreenSpaceOverlay canvas draws strictly in hierarchy order and ignores
/// render queues; a WorldSpace canvas is ordinary scene geometry, where the
/// queue decides. The menu font renders through
/// <c>TextMeshPro/Distance Field Overlay</c> at queue 4000 while the rest of
/// the UI sits at 3000, so that text draws after everything else in the canvas
/// — including any panel opened on top of it. Opening Customize Mission showed
/// the labels of the mission picker behind it straight through the panel.
///
/// <see cref="MaskedUiQueuePatch"/> handles the masked half of the same
/// mechanism everywhere. This is the unmasked half, and it is deliberately
/// scoped to the menu canvases: those are the ones with panels stacked over
/// each other, and unlike the flight HUD nothing in them is meant to draw on
/// top of its own canvas.
///
/// Unmasked graphics render with the shared font material rather than a
/// mask-specific copy, so the retimed materials are recorded and put back when
/// the menu goes away. Without that, a queue lowered for a menu would follow
/// the same font into the cockpit.
/// </summary>
internal static class MenuDrawOrderPatch
{
    private const int CanvasQueue = (int)RenderQueue.Transparent; // 3000

    private static readonly Dictionary<Material, int> RetimedMaterials = new();

    [HarmonyPatch(typeof(MaskableGraphic), nameof(MaskableGraphic.GetModifiedMaterial))]
    private static class MaskableGraphicPatch
    {
        [HarmonyPostfix]
        private static void Postfix(MaskableGraphic __instance, ref Material __result) =>
            ClampMenuQueue(__instance, __result);
    }

    [HarmonyPatch(typeof(TextMeshProUGUI), nameof(TextMeshProUGUI.GetModifiedMaterial))]
    private static class TextMeshProUGUIPatch
    {
        [HarmonyPostfix]
        private static void Postfix(TextMeshProUGUI __instance, ref Material __result) =>
            ClampMenuQueue(__instance, __result);
    }

    [HarmonyPatch(typeof(TMP_SubMeshUI), nameof(TMP_SubMeshUI.GetModifiedMaterial))]
    private static class SubMeshPatch
    {
        [HarmonyPostfix]
        private static void Postfix(TMP_SubMeshUI __instance, ref Material __result) =>
            ClampMenuQueue(__instance, __result);
    }

    private static void ClampMenuQueue(MaskableGraphic? graphic, Material? material)
    {
        if (graphic == null || material == null) return;
        if (material.renderQueue <= CanvasQueue) return;

        var canvas = graphic.canvas;
        if (canvas == null) return;

        // Only a world-space canvas orders by queue, so on the captured-menu
        // backend — where the canvas stays screen space — this is inert.
        var root = canvas.rootCanvas != null ? canvas.rootCanvas : canvas;
        if (root.renderMode != RenderMode.WorldSpace) return;
        if (root.GetComponent<NOVRMainMenuBehavior>() == null) return;

        if (!RetimedMaterials.ContainsKey(material))
        {
            RetimedMaterials[material] = material.renderQueue;
        }

        material.renderQueue = CanvasQueue;
    }

    /// <summary>
    /// Put every retimed material back. Called when the menu canvas goes away,
    /// which is also when the same fonts start being used by something else.
    /// </summary>
    internal static void RestoreRetimedMaterials()
    {
        foreach (var pair in RetimedMaterials)
        {
            if (pair.Key == null) continue;
            pair.Key.renderQueue = pair.Value;
        }

        RetimedMaterials.Clear();
    }
}
