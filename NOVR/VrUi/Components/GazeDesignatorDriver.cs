using UnityEngine;
using NOVR.VrUi.Capture;

namespace NOVR.VrUi.Components;

/// <summary>
/// Restore the base game's look-to-select, with the base game's code.
///
/// On the flat screen the target designator is a reticle that no code ever
/// moves — it sits at the view centre, free-look rotates the projection
/// camera, and world-projected unit markers slide across the screen until the
/// one you are looking at is under the reticle. `CombatHUD.TargetSelect` then
/// takes every marker within 100 screen pixels of it: radius, priority
/// scoring, painting, the off-screen arrow, turret aiming and the weapon-state
/// fades all read that one position.
///
/// Under the design eye the projection no longer rotates with the head, so a
/// fixed reticle would mean boresight-select. This driver moves the reticle
/// instead: the gaze direction projected through the design eye is exactly
/// "where the pilot is looking" in the captured HUD's screen space, so the
/// designator rides the gaze across the airframe panel and every consumer of
/// its position works unmodified. <c>FlightHud.HMDCenter</c> — the anchor the
/// game keeps at the view centre for cargo and sling UI — gets the same
/// treatment, because it means the same thing.
///
/// Looking past the panel edge keeps working the way the flat game's geometry
/// does: designator and markers share one projective space, so their pixel
/// distance stays meaningful beyond the visible frame, out to nearly 90
/// degrees off boresight. Behind that the designator is parked far away and
/// nothing selects.
///
/// Only the designator is driven. <c>FlightHud.HMDCenter</c> means "the view
/// centre" too, but it turned out to be the HeadMountedDisplay subtree's own
/// root (measured: driving it teleported the whole helmet suite into overlay
/// pixel coordinates and emptied the visor) — and with the visor head-locked
/// that rect already *is* at the view centre, cargo and sling UI included.
/// </summary>
public class GazeDesignatorDriver : NOVRBehaviour
{
    private static readonly Vector3 ParkedPosition = new(-100000f, -100000f, 0f);

    private bool _driving;
    private Vector3 _savedDesignatorLocal;
    private Transform? _designator;

    private void Update()
    {
        var projection = FlightHudCaptureBackend.ActiveDesignProjection;
        var want = projection != null && (CapturedHmd.GazeDesignator?.Value ?? true);

        if (!want)
        {
            if (_driving) StopDriving();
            return;
        }

        var combatHud = SceneSingleton<CombatHUD>.i;
        var designator = combatHud != null && combatHud.targetDesignator != null
            ? combatHud.targetDesignator.transform
            : null;

        if (designator == null)
        {
            if (_driving) StopDriving();
            return;
        }

        if (!_driving || !ReferenceEquals(designator, _designator))
        {
            if (_driving) StopDriving();
            _designator = designator;
            _savedDesignatorLocal = designator.localPosition;
            _driving = true;
        }

        var gaze = NOVRHeadsetData.Rotation * Vector3.forward;
        Vector3 position;
        if (gaze.z < 0.05f)
        {
            position = ParkedPosition;
        }
        else
        {
            var p = projection!.Value;
            var ndcX = p.m00 * (gaze.x / gaze.z) - p.m02;
            var ndcY = p.m11 * (gaze.y / gaze.z) - p.m12;
            position = new Vector3(
                (ndcX * 0.5f + 0.5f) * Screen.width,
                (ndcY * 0.5f + 0.5f) * Screen.height,
                0f);
        }

        designator.position = position;
    }

    private void OnDisable()
    {
        if (_driving) StopDriving();
    }

    /// <summary>
    /// Put the scene's own position back — the reticle is a static element the
    /// game never moves, so whatever we leave behind is where it stays.
    /// </summary>
    private void StopDriving()
    {
        _driving = false;
        if (_designator != null) _designator.localPosition = _savedDesignatorLocal;
        _designator = null;
    }
}
