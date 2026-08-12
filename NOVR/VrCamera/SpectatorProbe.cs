using UnityEngine;

namespace NOVR.VrCamera;

/// TEMPORARY probe. Harness only: forces the spectator (orbit) camera onto the
/// local aircraft a few seconds after spawn and logs where the camera actually
/// ends up relative to the unit it is following. Reverted once measured.
internal class SpectatorProbe : MonoBehaviour
{
    private float _forceAt = -1f;
    private bool _forced;
    private float _nextLog;

    private void Update()
    {
        if (!ModConfiguration.Instance.AutoStartMission.Value) return;

        var cam = SceneSingleton<CameraStateManager>.i;
        if (cam == null) return;
        if (!GameManager.GetLocalAircraft(out var aircraft) || aircraft == null) return;

        if (_forceAt < 0f) _forceAt = Time.unscaledTime + 4f;
        if (!_forced && Time.unscaledTime >= _forceAt)
        {
            _forced = true;
            cam.SetFollowingUnit(aircraft);
            Debug.Log("[NOVR-PROBE] forced spectate of the local aircraft");
        }

        if (!_forced || Time.unscaledTime < _nextLog) return;
        _nextLog = Time.unscaledTime + 1f;

        var unit = cam.followingUnit;
        if (unit == null)
        {
            Debug.Log($"[NOVR-PROBE] state={cam.currentState?.GetType().Name} followingUnit=null");
            return;
        }

        var pivot = cam.cameraPivot;
        var camPos = cam.transform.position;
        var unitPos = unit.transform.position;
        var eye = Camera.main != null ? Camera.main.transform.position : camPos;

        Debug.Log(
            $"[NOVR-PROBE] state={cam.currentState?.GetType().Name} " +
            $"dist={Vector3.Distance(camPos, unitPos):F2} maxRadius={unit.maxRadius:F2} " +
            $"eyeDist={Vector3.Distance(eye, unitPos):F2} " +
            $"camPos={camPos.ToString("F2")} unitPos={unitPos.ToString("F2")} " +
            $"eyePos={eye.ToString("F2")} " +
            $"camEuler={cam.transform.rotation.eulerAngles.ToString("F1")} " +
            $"pivotEuler={(pivot != null ? pivot.rotation.eulerAngles.ToString("F1") : "n/a")} " +
            $"unitEuler={unit.transform.rotation.eulerAngles.ToString("F1")} " +
            $"speed={(unit.rb != null ? unit.rb.velocity.magnitude : 0f):F1}");
    }
}
