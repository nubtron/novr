using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;

namespace NOVR.VrUi.HarmonyPatches;

/// <summary>
/// Makes stencil-masked UI content survive the screen-space → world-space canvas
/// conversion.
///
/// A <see cref="Mask"/> works by hierarchy order: the mask graphic writes the
/// stencil, its children draw testing <c>Equal</c> against it, and a pop
/// instruction on the same CanvasRenderer resets the stencil afterwards. A
/// ScreenSpaceOverlay canvas draws strictly in hierarchy order, so that holds.
/// A WorldSpace canvas is ordinary scene geometry, and there draw order comes
/// from the material's **render queue** — so any masked graphic whose material
/// sits in a later queue is issued *after* the stencil has already been popped
/// back to 0, its <c>Equal 1</c> test fails for every pixel, and it disappears.
///
/// Nuclear Option's menu font (Brass Mono) uses
/// <c>TextMeshPro/Distance Field Overlay</c>, queue 4000, while the rest of the
/// UI sits at 3000. Every masked label in that font therefore vanished in VR
/// while the rows, tag badges and unmasked labels around it drew normally —
/// most visibly the mission names in the Select Mission list, which are inside
/// the scroll view's Mask.
///
/// The fix pulls masked materials back into the canvas's own queue, which
/// restores hierarchy order between the mask, its content and the pop.
/// Unmasked materials are left alone: their <c>Always</c> stencil test does not
/// care when they draw, and Overlay-queue text outside a mask is deliberately
/// on top.
///
/// TextMeshPro overrides <c>GetModifiedMaterial</c> in two places rather than
/// inheriting <see cref="MaskableGraphic"/>'s, so all three are patched.
/// </summary>
internal static class MaskedUiQueuePatch
{
    // The queue every non-Overlay UI material in the game's canvases uses, and
    // the one the mask graphic itself writes the stencil at.
    private const int CanvasQueue = (int)RenderQueue.Transparent; // 3000

    private static readonly int StencilCompId = Shader.PropertyToID("_StencilComp");

    [HarmonyPatch(typeof(MaskableGraphic), nameof(MaskableGraphic.GetModifiedMaterial))]
    private static class MaskableGraphicPatch
    {
        [HarmonyPostfix]
        private static void Postfix(MaskableGraphic __instance, ref Material __result) =>
            ClampMaskedQueue(__instance, __result);
    }

    [HarmonyPatch(typeof(TextMeshProUGUI), nameof(TextMeshProUGUI.GetModifiedMaterial))]
    private static class TextMeshProUGUIPatch
    {
        [HarmonyPostfix]
        private static void Postfix(TextMeshProUGUI __instance, ref Material __result) =>
            ClampMaskedQueue(__instance, __result);
    }

    [HarmonyPatch(typeof(TMP_SubMeshUI), nameof(TMP_SubMeshUI.GetModifiedMaterial))]
    private static class SubMeshPatch
    {
        [HarmonyPostfix]
        private static void Postfix(TMP_SubMeshUI __instance, ref Material __result) =>
            ClampMaskedQueue(__instance, __result);
    }

    private static void ClampMaskedQueue(MaskableGraphic? graphic, Material? material)
    {
        if (graphic == null || material == null) return;

        // Only world-space canvases order their draws by render queue; leaving
        // screen-space canvases untouched keeps this inert outside VR.
        var canvas = graphic.canvas;
        if (canvas == null || canvas.renderMode != RenderMode.WorldSpace) return;

        if (material.renderQueue <= CanvasQueue) return;
        if (!material.HasProperty(StencilCompId)) return;

        // Anything still comparing Always is unmasked and draws correctly
        // wherever it lands in the frame.
        if ((int)material.GetFloat(StencilCompId) == (int)CompareFunction.Always) return;

        // This material is the mask-specific variant the stencil machinery
        // created for this (base material, stencil id) pair, so lowering its
        // queue only affects masked drawing.
        material.renderQueue = CanvasQueue;
    }
}
