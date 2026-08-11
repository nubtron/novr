using System;
using UnityEngine;
using UnityEngine.UI;

namespace NOVR.VrUi.SpecialBehavior;

/// <summary>
/// Restores the one thing a screen-space canvas provided for free and a
/// world-space canvas does not: an edge.
///
/// The game parks UI it does not currently want by moving it outside the canvas
/// rect — the gameplay MFD screens sit at local x = ±2560 on a 1920-wide canvas
/// while closed. On a ScreenSpaceOverlay canvas that is enough, because there is
/// nothing to rasterise them into; the screen *is* the clip. NOVR converts these
/// canvases to WorldSpace, where the rect is just a number and the geometry is
/// ordinary geometry, so the parked panels become real objects hanging beside
/// the pilot. Seen from the cockpit they read as two faint white rectangles far
/// left and right, apparently in the HUD plane, because that is exactly where
/// they are — the MFD panel backgrounds, white, 450×650, viewed nearly edge-on.
///
/// So: anything whose rect lies entirely outside the canvas rect gets culled.
/// Entirely outside, not partially — a partially visible element is something
/// the pilot is meant to see part of, and hiding it would be a worse bug than
/// the one being fixed. Culling through CanvasRenderer.cull is what RectMask2D
/// itself uses; a RectMask2D would also clip the partial cases, but it works by
/// driving a shader clip rect, and this fork has already been bitten once by UI
/// masking that did not survive the world-space conversion (see the render-queue
/// notes in the research repo). Culling depends on no shader support at all, and
/// its worst failure is that something stays visible.
/// </summary>
public class OffscreenGraphicCuller : MonoBehaviour
{
    /// <summary>
    /// How often the graphic list is rebuilt. The cull test itself runs every
    /// frame against the cached list, so a panel sliding back into view appears
    /// immediately; only newly *created* graphics wait for a rescan.
    /// </summary>
    private const float RescanInterval = 1f;

    /// <summary>
    /// Slack in canvas units, so an element deliberately flush with the edge is
    /// never culled by float error.
    /// </summary>
    private const float Margin = 2f;

    private static readonly Vector3[] Corners = new Vector3[4];

    private RectTransform _canvasRect;
    private Graphic[] _graphics = Array.Empty<Graphic>();
    private float _nextRescan;

    private void Awake()
    {
        _canvasRect = GetComponent<RectTransform>();
    }

    private void LateUpdate()
    {
        if (_canvasRect == null) return;

        if (Time.unscaledTime >= _nextRescan)
        {
            _nextRescan = Time.unscaledTime + RescanInterval;
            _graphics = GetComponentsInChildren<Graphic>(true);
        }

        var bounds = _canvasRect.rect;
        bounds.xMin -= Margin;
        bounds.xMax += Margin;
        bounds.yMin -= Margin;
        bounds.yMax += Margin;

        foreach (var graphic in _graphics)
        {
            if (graphic == null) continue;

            var renderer = graphic.canvasRenderer;
            if (renderer == null) continue;

            var cull = IsEntirelyOutside(bounds, graphic.rectTransform);

            // Only write on a change: CanvasRenderer.cull dirties the batch when
            // it is set, even to the value it already had.
            if (renderer.cull != cull)
            {
                renderer.cull = cull;
            }
        }
    }

    private bool IsEntirelyOutside(Rect bounds, RectTransform target)
    {
        if (target == null) return false;

        // Corners go through world space rather than being read from the local
        // rect, because the element may be scaled, rotated or nested several
        // layouts deep; the only position that matters is the one it renders at.
        target.GetWorldCorners(Corners);

        float minX = float.MaxValue, maxX = float.MinValue;
        float minY = float.MaxValue, maxY = float.MinValue;
        for (var i = 0; i < 4; i++)
        {
            var local = _canvasRect.InverseTransformPoint(Corners[i]);
            if (local.x < minX) minX = local.x;
            if (local.x > maxX) maxX = local.x;
            if (local.y < minY) minY = local.y;
            if (local.y > maxY) maxY = local.y;
        }

        return maxX < bounds.xMin || minX > bounds.xMax ||
               maxY < bounds.yMin || minY > bounds.yMax;
    }
}
