using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using Cysharp.Threading.Tasks;
using HarmonyLib;
using NOVR.VrCamera;
using NuclearOption.Networking;
using NuclearOption.SavedMission;
using UnityEngine;

namespace NOVR;

/// <summary>
/// Test-harness driver: starts a mission from the main menu without anyone at
/// the keyboard, waits until the player actually has an aircraft, then fires a
/// few buffer dumps (and, with RenderDoc injected, GPU captures) and writes a
/// completion marker.
///
/// This is what turns "wear the headset, fly, squint at the HUD, form an
/// opinion" into an unattended run whose output can be diffed. It only does
/// anything when [Debug] Auto Start Mission is on; the default is off, so a
/// normal play session never sees it.
///
/// The mission is launched the same way NOVR's own native menu launches one
/// (<see cref="NOVR.VrUi.Native.NativeSinglePlayerMissionPanel"/>):
/// MissionManager.SetMission followed by StartHost with an Offline socket.
/// Driving the real API rather than synthesising menu clicks means the harness
/// does not break every time the menu layout changes.
/// </summary>
public class AutoStartMission : MonoBehaviour
{
    private const string DoneMarkerName = "harness.done";

    // The main menu is still wiring itself up for a while after the scene
    // loads: an earlier version fired at t+5s and the menu logged "All Task
    // finished" *after* the mission had started, which looked like the launch
    // was being overwritten. Start later and retry rather than guessing a
    // single delay.
    private const float FirstAttemptDelay = 12f;
    private const float RetryInterval = 3f;
    private const float SpawnRetryInterval = 4f;

    private bool _launched;
    private bool _spawned;
    private bool _missionRunning;
    private bool _finished;
    private float _nextAttempt = FirstAttemptDelay;
    private float _nextSpawnAttempt;
    private float _nextDumpAt;
    private int _dumpsRemaining;
    private string _lastSpawnBlocker;
    private bool _listedMissions;

    // Frames between setting the head pose and dumping. Three is empirical
    // slack, not a measured minimum: the pose has to reach the runtime, come
    // back through InputTracking, and be consumed by a camera update.
    private const int YawSettleFrames = 3;
    private float[] _yaws;
    private bool _yawApplied;
    private int _yawSettleFrames;

    private void Awake()
    {
        if (!ModConfiguration.Instance.AutoStartMission.Value)
        {
            enabled = false;
            return;
        }

        _yaws = ParseYaws(ModConfiguration.Instance.AutoDumpYaws.Value);
        _dumpsRemaining = _yaws != null ? _yaws.Length : ModConfiguration.Instance.AutoDumpCount.Value;
        ClearDoneMarker();
        Debug.Log("[NOVR-HARNESS] Auto Start Mission enabled — the game will start a mission and dump unattended." +
                  (_yaws != null ? $" Yaw sweep: {string.Join(", ", Array.ConvertAll(_yaws, y => $"{y:0.#}°"))}." : ""));
    }

    private void Update()
    {
        if (_finished) return;

        if (!_launched)
        {
            if (Time.unscaledTime < _nextAttempt) return;
            _nextAttempt = Time.unscaledTime + RetryInterval;
            TryLaunchMission();
            return;
        }

        if (!_missionRunning)
        {
            // "Mission running" means the player owns an aircraft — the HUD
            // canvases NOVR translates only exist from that point on, so
            // dumping earlier would capture an empty frame and look like a bug.
            if (!GameManager.GetLocalAircraft(out var aircraft) || aircraft == null)
            {
                // Starting a mission does not put you in a cockpit; it drops
                // you at the airbase/aircraft selection step. Nothing spawns
                // until that is answered, so answer it.
                if (!_spawned && Time.unscaledTime >= _nextSpawnAttempt)
                {
                    _nextSpawnAttempt = Time.unscaledTime + SpawnRetryInterval;
                    _spawned = TryRequestSpawn();
                }
                return;
            }

            _missionRunning = true;
            if (ApproachRequested)
            {
                Debug.Log("[NOVR-HARNESS] Mission running, local aircraft acquired. Placing it on final approach.");
                return;
            }

            _nextDumpAt = Time.unscaledTime + ModConfiguration.Instance.AutoDumpDelay.Value;
            Debug.Log($"[NOVR-HARNESS] Mission running, local aircraft acquired. First dump in {ModConfiguration.Instance.AutoDumpDelay.Value:0.#}s.");
            return;
        }

        // Held every frame, including through the dumps: left alone, a
        // teleported aircraft with idle engines is on the ground within seconds
        // and the yaw sweep's later frames would show a different situation
        // from its first.
        if (ApproachRequested && !UpdateApproach()) return;

        if (Time.unscaledTime < _nextDumpAt) return;

        FireDump();
    }

    // ------------------------------------------------------------ approach mode

    /// <summary>How far out on the extended centreline the aircraft is held.</summary>
    private const float ApproachDistance = 2000f;

    /// <summary>
    /// The gradient the game's own glideslope symbology is drawn on
    /// (<c>RunwayUsage.GetGlideslopeAimpoint</c> builds its direction from
    /// <c>-direction + up * Length * 0.06</c>). Using the same number puts the
    /// aircraft on the glideslope rather than merely near it, so the glideslope
    /// line is at its neutral position instead of pinned to one extreme.
    /// </summary>
    private const float GlideslopeGradient = 0.06f;

    /// <summary>
    /// How long to wait for the game to agree that this is a landing before
    /// giving up and dumping anyway. It is a slow update on a 0.5 s tick, so
    /// this is generous by two orders of magnitude — if it has not happened by
    /// now it is not going to, and the diagnosis is worth more than the wait.
    /// </summary>
    private const float ApproachTimeout = 20f;

    private static readonly FieldInfo LandingField =
        AccessTools.Field(typeof(AirbaseOverlay), "landing");

    private bool _approachPlaced;
    private bool _approachSettled;
    private float _approachDeadline;
    private Airbase.Runway.RunwayUsage? _approachUsage;
    private AirbaseOverlay _airbaseOverlay;
    private string _lastApproachBlocker;

    private static bool ApproachRequested => ModConfiguration.Instance.AutoApproach.Value;

    /// <summary>
    /// Put the aircraft on final and keep it there. Returns true once the game
    /// has accepted it as a landing (or once waiting for that has been given
    /// up on), which is when the dumps may start.
    ///
    /// <para>The landing symbology — runway outline, glideslope, airbase marker
    /// — is drawn only while <c>AirbaseOverlay</c> has decided the player is
    /// landing, and that decision has five separate conditions. A harness that
    /// spawns parked in a hangar meets none of them, so none of that symbology
    /// has ever appeared in a harness frame. This is what puts it on screen.</para>
    ///
    /// <para>The aircraft is not flown there; it is placed and pinned. Flying
    /// an approach would need an autopilot and would make every run a different
    /// frame. Pinning costs realism the harness does not need — nothing here
    /// depends on the airframe's dynamics, only on where it is and what the
    /// game believes about it.</para>
    /// </summary>
    private bool UpdateApproach()
    {
        if (!GameManager.GetLocalAircraft(out var aircraft) || aircraft == null)
        {
            return NotApproaching("no local aircraft");
        }

        if (!_approachPlaced)
        {
            if (!PlaceOnApproach(aircraft)) return false;
            _approachPlaced = true;
            _approachDeadline = Time.unscaledTime + ApproachTimeout;
        }

        HoldOnApproach(aircraft);

        if (_approachSettled) return true;

        if (IsLanding())
        {
            _approachSettled = true;
            _nextDumpAt = Time.unscaledTime + ModConfiguration.Instance.AutoDumpDelay.Value;
            Debug.Log(
                "[NOVR-HARNESS] Approach accepted — the game is drawing the landing symbology. " +
                $"First dump in {ModConfiguration.Instance.AutoDumpDelay.Value:0.#}s.");
            return true;
        }

        if (Time.unscaledTime < _approachDeadline) return false;

        // Dump anyway. A run that produces frames and a reason is worth more
        // than one that times out with neither.
        _approachSettled = true;
        _nextDumpAt = Time.unscaledTime + ModConfiguration.Instance.AutoDumpDelay.Value;
        Debug.LogWarning(
            "[NOVR-HARNESS] Approach was never accepted as a landing; dumping anyway. " + DescribeApproach(aircraft));
        return true;
    }

    private bool PlaceOnApproach(Aircraft aircraft)
    {
        try
        {
            var hq = aircraft.NetworkHQ;
            if (hq == null) return NotApproaching("aircraft has no faction HQ yet");

            // The same query AirbaseOverlay builds, so the runway this picks is
            // the runway the overlay will pick a moment later. A different query
            // could select a different runway and the placement would then be an
            // approach to somewhere the game is not watching.
            var parameters = aircraft.GetAircraftParameters();
            var maxWeight = aircraft.definition.aircraftInfo.maxWeight;
            var query = new RunwayQuery
            {
                RunwayType = RunwayQueryType.Any,
                MinSize = parameters.takeoffDistance,
                TailHook = aircraft.weaponManager != null && aircraft.weaponManager.HasTailHook(),
                LandingSpeed = maxWeight > 0f
                    ? Mathf.Sqrt(aircraft.GetMass() / maxWeight) * parameters.takeoffSpeed
                    : parameters.takeoffSpeed,
            };

            var airbase = hq.GetNearestAirbase(aircraft.transform.position, query);
            if (airbase == null) return NotApproaching("no friendly airbase with a suitable runway");

            var usage = airbase.RequestLanding(aircraft, query);
            if (!usage.HasValue) return NotApproaching($"airbase '{airbase.name}' offered no landing runway");

            _approachUsage = usage;

            aircraft.transform.SetPositionAndRotation(ApproachPosition(aircraft), ApproachRotation());
            if (aircraft.rb != null)
            {
                aircraft.rb.velocity = ApproachVelocity(query.LandingSpeed);
                aircraft.rb.angularVelocity = Vector3.zero;
            }

            aircraft.SetGear(deployed: true);

            // Everything the overlay does is gated on having taken off — before
            // that it is drawing taxi guidance instead. Nothing sets it for an
            // aircraft that was teleported into the air.
            if (aircraft.pilots != null && aircraft.pilots.Length > 0 && aircraft.pilots[0] != null)
            {
                aircraft.pilots[0].flightInfo.HasTakenOff = true;
            }

            // Recomputes radarAlt from a downward linecast; without it the
            // aircraft keeps the altitude it had in the hangar for a tick and
            // the overlay's radarAlt > 20 test fails on the first slow update.
            aircraft.SpawnedInPosition();

            Debug.Log(
                $"[NOVR-HARNESS] Placed on final: runway {usage.Value.GetName()} at '{airbase.name}', " +
                $"{ApproachDistance:0} m out, {ApproachDistance * GlideslopeGradient:0} m above the threshold, " +
                $"gear down, {ApproachVelocity(query.LandingSpeed).magnitude:0} m/s.");
            return true;
        }
        catch (Exception e)
        {
            return NotApproaching($"placement threw: {e.Message}");
        }
    }

    /// <summary>
    /// Recomputed from the runway's own transform every frame rather than
    /// stored, so a floating-origin shift moves the hold point with the world
    /// instead of leaving it a kilometre behind.
    /// </summary>
    private Vector3 ApproachPosition(Aircraft aircraft)
    {
        var usage = _approachUsage.Value;
        var threshold = usage.GetStart().position;
        var height = ApproachDistance * GlideslopeGradient + aircraft.definition.spawnOffset.y;
        return threshold - usage.GetDirection().normalized * ApproachDistance + Vector3.up * height;
    }

    private Quaternion ApproachRotation() =>
        Quaternion.LookRotation(_approachUsage.Value.GetDirection().normalized, Vector3.up);

    private Vector3 ApproachVelocity(float landingSpeed) =>
        _approachUsage.Value.GetDirection().normalized * Mathf.Max(60f, landingSpeed * 1.3f);

    private void HoldOnApproach(Aircraft aircraft)
    {
        if (_approachUsage == null) return;

        aircraft.transform.SetPositionAndRotation(ApproachPosition(aircraft), ApproachRotation());
        if (aircraft.rb != null)
        {
            aircraft.rb.velocity = ApproachVelocity(aircraft.GetAircraftParameters().takeoffSpeed);
            aircraft.rb.angularVelocity = Vector3.zero;
        }

        if (!aircraft.gearDeployed) aircraft.SetGear(deployed: true);
    }

    private bool IsLanding()
    {
        if (_airbaseOverlay == null) _airbaseOverlay = FindOverlay();
        if (_airbaseOverlay == null || LandingField == null) return false;
        return (bool)LandingField.GetValue(_airbaseOverlay);
    }

    /// <summary>
    /// Finds the overlay whether or not it is switched on. <c>FindObjectOfType</c>
    /// skips inactive objects, so an overlay that is present but disabled reads
    /// as absent — which is the difference between "the game decided not to
    /// call this a landing" and "the thing that makes that decision is not
    /// running", and those need opposite fixes.
    /// </summary>
    private static AirbaseOverlay FindOverlay()
    {
        foreach (var candidate in Resources.FindObjectsOfTypeAll<AirbaseOverlay>())
        {
            if (candidate != null && candidate.gameObject.scene.IsValid()) return candidate;
        }

        return null;
    }

    private static string DescribeOverlay()
    {
        var found = Resources.FindObjectsOfTypeAll<AirbaseOverlay>();
        var parts = new List<string>();
        foreach (var candidate in found)
        {
            if (candidate == null || !candidate.gameObject.scene.IsValid()) continue;

            var path = candidate.name;
            for (var t = candidate.transform.parent; t != null; t = t.parent) path = t.name + "/" + path;
            parts.Add($"{path} active={candidate.gameObject.activeInHierarchy} " +
                      $"selfActive={candidate.gameObject.activeSelf} enabled={candidate.enabled}");
        }

        return parts.Count == 0 ? "overlay=<none in any loaded scene>" : "overlay: " + string.Join(" | ", parts);
    }

    /// <summary>
    /// Every condition <c>AirbaseOverlay.UpdateNearestAirbase</c> tests before
    /// it will call this a landing, with its actual value. Five conditions and
    /// one log line: a "landing never started" that does not say which of the
    /// five failed costs a full run to narrow down.
    /// </summary>
    private string DescribeApproach(Aircraft aircraft)
    {
        try
        {
            var usage = _approachUsage!.Value;
            var runway = usage.Runway;
            var direction = usage.GetDirection().normalized;
            var takenOff = aircraft.pilots != null && aircraft.pilots.Length > 0 && aircraft.pilots[0] != null &&
                           aircraft.pilots[0].flightInfo.HasTakenOff;

            return $"hasTakenOff={takenOff} radarAlt={aircraft.radarAlt:0.0} (needs >20) " +
                   $"gearDeployed={aircraft.gearDeployed} " +
                   $"verticalLanding={aircraft.GetAircraftParameters().verticalLanding} " +
                   $"onApproach={runway.AircraftOnApproach(aircraft, 2500f, excludeBetweenEndpoints: true)} " +
                   $"alignment={Mathf.Abs(Vector3.Dot(aircraft.transform.forward, direction)):0.00} (needs >0.80) " +
                   $"toStart={Vector3.Distance(aircraft.transform.position, runway.Start.position):0} m " +
                   $"toEnd={Vector3.Distance(aircraft.transform.position, runway.End.position):0} m " +
                   DescribeOverlay();
        }
        catch (Exception e)
        {
            return $"(could not describe the approach: {e.Message})";
        }
    }

    private bool NotApproaching(string reason)
    {
        if (reason != _lastApproachBlocker)
        {
            _lastApproachBlocker = reason;
            Debug.Log($"[NOVR-HARNESS] Waiting to place on approach: {reason}.");
        }

        return false;
    }

    private void TryLaunchMission()
    {
        try
        {
            MissionGroup.Init();
            var missions = MissionSaveLoad
                .QuickLoadMany(MissionGroup.All.GetMissions())
                .Where(entry => HasSinglePlayerTag(entry.mission))
                .ToList();

            if (missions.Count == 0)
            {
                Debug.Log("[NOVR-HARNESS] No single-player missions available yet; retrying.");
                return;
            }

            if (!_listedMissions)
            {
                _listedMissions = true;
                Debug.Log(
                    "[NOVR-HARNESS] Available single-player missions: " +
                    string.Join(" | ", missions.Select(entry => entry.key.ToString())));
            }

            var wanted = ModConfiguration.Instance.AutoStartMissionName.Value;
            var chosen = SelectMission(missions, wanted);
            if (!chosen.key.TryLoad(out var mission, out var error))
            {
                Debug.LogWarning($"[NOVR-HARNESS] Failed to load mission '{chosen.key}': {error}");
                return;
            }

            if (NetworkManagerNuclearOption.i == null)
            {
                Debug.Log("[NOVR-HARNESS] NetworkManager not ready yet; retrying.");
                return;
            }

            MissionManager.SetMission(mission, checkIfSame: false);
            NetworkManagerNuclearOption.i.StartHost(
                new HostOptions(SocketType.Offline, GameState.SinglePlayer, mission.MapKey));

            _launched = true;
            Debug.Log($"[NOVR-HARNESS] Started mission '{chosen.key}'.");
        }
        catch (Exception e)
        {
            // The menu can throw while it is still wiring itself up; that is a
            // retry, not a failure.
            Debug.Log($"[NOVR-HARNESS] Mission launch attempt failed (will retry): {e.Message}");
        }
    }

    /// <summary>
    /// Spawn the local player into an aircraft, the way the aircraft selection
    /// screen ultimately does.
    ///
    /// Driving <c>AircraftSelectionMenu</c> itself would mean faking UI state —
    /// selection index, loadout dropdowns, preview models. Its
    /// <c>FlyAircraft()</c> just resolves to
    /// <c>Spawner.RequestSpawnAtAirbase</c>, which is public and takes plain
    /// data, so the harness calls that directly and skips the UI entirely.
    ///
    /// The server-side check (<c>Spawner.AllowedToSpawn</c>) only requires that
    /// the player has a faction HQ, that the airbase belongs to it, that the
    /// aircraft is not restricted, and that the player owns the airframe — it
    /// does not inspect the loadout, so a default one is fine. We mirror those
    /// conditions here so a rejection shows up as a log line naming the reason
    /// rather than a silent no-op.
    /// </summary>
    private bool TryRequestSpawn()
    {
        try
        {
            if (!GameManager.GetLocalPlayer<Player>(out var player) || player == null)
            {
                return NotReady("no local player yet");
            }

            if (player.HQ == null && !TryJoinFaction(player))
            {
                return NotReady("local player has no faction HQ yet");
            }

            var spawner = NetworkSceneSingleton<Spawner>.i;
            if (spawner == null)
            {
                return NotReady("Spawner scene singleton not available yet");
            }

            // Counted rather than just filtered: "nothing is spawnable" has
            // four different causes and they need different fixes, so the log
            // has to say which one it was.
            int airbases = 0, owned = 0, offered = 0, noHangar = 0, notOwned = 0;

            foreach (var airbase in player.HQ.GetAirbases())
            {
                airbases++;
                if (airbase == null || airbase.CurrentHQ != player.HQ) continue;
                owned++;

                foreach (var definition in airbase.GetAvailableAircraft())
                {
                    if (definition == null) continue;
                    offered++;

                    if (!airbase.CanSpawnAircraft(definition)) { noHangar++; continue; }
                    if (!player.OwnsAirframe(definition, includeReserved: true)) { notOwned++; continue; }

                    spawner
                        .RequestSpawnAtAirbase(airbase, definition, default, new Loadout(), 1f)
                        .Forget();
                    Debug.Log($"[NOVR-HARNESS] Requested spawn: {definition.unitName} at {airbase.name}.");
                    return true;
                }
            }

            return NotReady(
                $"no spawnable aircraft (airbases={airbases} ours={owned} " +
                $"offered={offered} noHangar={noHangar} notOwned={notOwned})");
        }
        catch (Exception e)
        {
            // The mission scene is still assembling for a while after the host
            // starts; treat anything thrown here as "not ready".
            Debug.Log($"[NOVR-HARNESS] Spawn attempt failed (will retry): {e.Message}");
            return false;
        }
    }

    /// <summary>
    /// Join a faction, which the join menu normally does before you ever reach
    /// aircraft selection. Without it <c>Player.HQ</c> stays null and
    /// <c>Spawner.AllowedToSpawn</c> rejects every request, so no aircraft ever
    /// spawns and the run just times out.
    /// </summary>
    private bool TryJoinFaction(Player player)
    {
        var factions = FindObjectsOfType<FactionHQ>();
        foreach (var hq in factions)
        {
            if (hq == null || hq.preventJoin) continue;

            player.SetFaction(hq);
            if (player.HQ == null) continue;

            Debug.Log($"[NOVR-HARNESS] Joined faction '{hq.faction?.name ?? hq.name}'.");
            return true;
        }

        if (factions.Length == 0) NotReady("no FactionHQ in the scene yet");
        return false;
    }

    /// <summary>
    /// Log why the spawn is not possible yet, once per distinct reason.
    /// Silent early returns here cost a full run to diagnose: the harness just
    /// times out with no output and no indication which precondition failed.
    /// </summary>
    private bool NotReady(string reason)
    {
        if (reason != _lastSpawnBlocker)
        {
            _lastSpawnBlocker = reason;
            Debug.Log($"[NOVR-HARNESS] Waiting to spawn: {reason}.");
        }

        return false;
    }

    private static (MissionKey key, MissionQuickLoad mission) SelectMission(
        List<(MissionKey key, MissionQuickLoad mission)> missions,
        string wanted)
    {
        if (!string.IsNullOrWhiteSpace(wanted))
        {
            foreach (var entry in missions)
            {
                if (entry.key.ToString().IndexOf(wanted, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return entry;
                }
            }

            Debug.LogWarning($"[NOVR-HARNESS] No mission matched '{wanted}'; falling back to Free Flight.");
        }

        // Prefer a normal built-in mission. Free Flight looks like the obvious
        // choice (fast to load, nothing shooting at you) but it does not appear
        // to offer an airbase hangar spawn, and the harness spawns by calling
        // Spawner.RequestSpawnAtAirbase — so it can start the mission and then
        // never get into a cockpit. A built-in mission spawns from an airbase.
        foreach (var entry in missions)
        {
            if (SameGroup(entry.key.Group, MissionGroup.BuiltIn)) return entry;
        }

        foreach (var entry in missions)
        {
            if (SameGroup(entry.key.Group, MissionGroup.Default)) return entry;
        }

        return missions[0];
    }

    private static bool SameGroup(MissionGroup left, MissionGroup right)
    {
        return ReferenceEquals(left, right) ||
               string.Equals(left?.Name, right?.Name, StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasSinglePlayerTag(MissionQuickLoad mission)
    {
        var tags = mission.missionSettings.Tags;
        return tags != null && tags.Any(tag => tag.Equals(MissionTag.SinglePlayer));
    }

    private void FireDump()
    {
        var total = _yaws != null ? _yaws.Length : ModConfiguration.Instance.AutoDumpCount.Value;
        var index = total - _dumpsRemaining + 1;

        var label = $"auto-{index}";
        if (_yaws != null)
        {
            var yaw = _yaws[index - 1];

            // Set the pose, then dump some frames later. The runtime pose has
            // to round-trip through the XR subsystem before NOVR's camera, HUD
            // reference direction and gaze all follow it; dumping in the same
            // frame captures the previous view under the new label, which is
            // the most misleading output this harness could produce.
            if (!_yawApplied)
            {
                HarnessViewPose.SetYaw(yaw);
                _yawApplied = true;
                _yawSettleFrames = YawSettleFrames;
                return;
            }

            if (_yawSettleFrames > 0)
            {
                _yawSettleFrames--;
                return;
            }

            label = $"auto-{index}-yaw{yaw:0.#}";
        }

        VrDebugDump.Request(label);
        if (ModConfiguration.Instance.RenderDocCaptureOnDump.Value)
        {
            RenderDocCapture.TryTriggerCapture();
        }

        _yawApplied = false;
        _dumpsRemaining--;
        Debug.Log($"[NOVR-HARNESS] Fired dump {index}/{total}" +
                  (_yaws != null ? $" at yaw {_yaws[index - 1]:0.#}° (measured {HarnessViewPose.MeasuredYaw():0.#}°)." : "."));

        if (_dumpsRemaining > 0)
        {
            _nextDumpAt = Time.unscaledTime + ModConfiguration.Instance.AutoDumpDelay.Value;
            return;
        }

        Finish();
    }

    /// <summary>
    /// Parse [Debug] Auto Dump Yaws. Returns null when it is empty, which is
    /// the ordinary "dump straight ahead" path.
    /// </summary>
    private static float[] ParseYaws(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var parts = raw.Split(',');
        var yaws = new List<float>(parts.Length);
        foreach (var part in parts)
        {
            if (float.TryParse(part.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var yaw))
            {
                yaws.Add(yaw);
            }
            else
            {
                Debug.LogWarning($"[NOVR-HARNESS] Ignoring unparseable yaw '{part.Trim()}'.");
            }
        }

        return yaws.Count > 0 ? yaws.ToArray() : null;
    }

    /// <summary>
    /// Write a marker the WSL-side harness watches for. Without it the driver
    /// can only poll for output files and guess when the run is done, which
    /// turns "the mod crashed" into a full-length timeout every time.
    /// </summary>
    private void Finish()
    {
        _finished = true;
        try
        {
            var path = Path.Combine(NOVRPlugin.ModFolderPath, DoneMarkerName);
            var dumps = _yaws != null ? _yaws.Length : ModConfiguration.Instance.AutoDumpCount.Value;
            File.WriteAllText(path, $"dumps={dumps}\nrenderdoc={RenderDocCapture.IsAvailable}\n");
            Debug.Log($"[NOVR-HARNESS] Run complete, wrote {DoneMarkerName}.");
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[NOVR-HARNESS] Failed to write {DoneMarkerName}: {e.Message}");
        }
    }

    private static void ClearDoneMarker()
    {
        try
        {
            var path = Path.Combine(NOVRPlugin.ModFolderPath, DoneMarkerName);
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // A stale marker only costs the driver one confused run; not worth
            // aborting startup over.
        }
    }
}
