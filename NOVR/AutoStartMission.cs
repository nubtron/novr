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
    private bool _creditedAirframe;
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

        DismissDialogue();

        // Held every frame, including through the dumps: left alone, a
        // teleported aircraft with idle engines is on the ground within seconds
        // and the yaw sweep's later frames would show a different situation
        // from its first.
        if (ApproachRequested && !UpdateApproach()) return;

        if (Time.unscaledTime < _nextDumpAt) return;

        FireDump();
    }

    // ------------------------------------------------------------ briefing dialogue

    private static readonly MethodInfo DialoguePressMethod =
        AccessTools.Method(typeof(DialogueBox), "InvokeButtonPress");

    private const float DialogueRetryInterval = 0.5f;

    private float _nextDialogueAttempt;
    private int _dialoguesDismissed;

    /// <summary>
    /// Press the button on any briefing dialogue the mission puts up.
    ///
    /// <para>This is not cosmetic. <c>DialogueBox.EnableBox</c> sets
    /// <c>Time.timeScale = 0</c> in single player, so a mission that opens with
    /// a briefing leaves an unattended run in a **paused game** for its entire
    /// length — physics stopped, animation stopped, and the game's slow-update
    /// system dead, because its scheduler compares against
    /// <c>Time.timeSinceLevelLoad</c>, which does not advance at timescale zero.
    /// Anything decided on a slow update (which includes every landing and
    /// takeoff decision the airbase overlay makes) therefore never happens.
    /// Every harness frame captured before this was a frame of a paused
    /// game.</para>
    ///
    /// <para>The button is pressed rather than the box hidden: pressing raises
    /// <c>ButtonPressed</c> with the dialogue's id, which is how the mission
    /// advances its own script. <c>Hide()</c> would clear the box and restore
    /// the timescale while leaving the mission waiting forever for an answer.</para>
    /// </summary>
    private void DismissDialogue()
    {
        if (DialoguePressMethod == null) return;

        // Only while actually stopped. Pressing raises the event but does not
        // clear CurrentId — the mission's own handler does — so without this
        // the harness would keep pressing a dialogue that has already been
        // answered and is only waiting to be replaced.
        if (Time.timeScale > 0f) return;
        if (Time.unscaledTime < _nextDialogueAttempt) return;
        _nextDialogueAttempt = Time.unscaledTime + DialogueRetryInterval;

        foreach (var box in Resources.FindObjectsOfTypeAll<DialogueBox>())
        {
            if (box == null || !box.gameObject.scene.IsValid() || !box.CurrentId.HasValue) continue;

            try
            {
                // The title is logged because pressing blind is a real risk: a
                // dialogue that appears mid-run may be the mission telling you
                // it has failed, and the button then agrees with it. Seeing
                // what was agreed to afterwards is the difference between
                // "the run ended" and knowing why.
                var title = DialogueTitle(box);
                DialoguePressMethod.Invoke(box, null);
                _dialoguesDismissed++;
                Debug.Log($"[NOVR-HARNESS] Dismissed dialogue {_dialoguesDismissed} " +
                          $"(id {box.CurrentId}, \"{title}\"); timescale is now {Time.timeScale:0.##}.");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[NOVR-HARNESS] Could not dismiss a briefing dialogue: {e.Message}");
            }

            return;
        }
    }

    // ------------------------------------------------------------ approach mode

    /// <summary>
    /// How far out on the extended centreline the aircraft is held. The default
    /// is kept short on purpose: at 2 km both a tutorial and a campaign mission
    /// treated the placement as leaving the mission area and put up a failure
    /// dialogue within two seconds. Inside the airbase's own radius nothing
    /// objects, and the overlay's approach test only needs 2.5 km.
    /// </summary>
    private static float ApproachDistance => ModConfiguration.Instance.AutoApproachDistance.Value;

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

    /// <summary>Grace after the HUD binds before the aircraft is moved.</summary>
    private const float CockpitSettleSeconds = 3f;

    /// <summary>
    /// Least height above the ground the hold point is allowed to have. Sized
    /// to be clear of the terrain collider under any airbase the game ships
    /// rather than to be a realistic approach height: the aircraft here is
    /// pinned, and the only thing this number buys is that nothing touches it.
    /// </summary>
    private const float MinTerrainClearance = 120f;

    /// <summary>
    /// The most the hold will change the aircraft's velocity by, in g. Well
    /// under the 20 g at which <c>Pilot</c> starts taking damage, because the
    /// damage term is quadratic and the margin costs only a fraction of a
    /// second of ramp.
    /// </summary>
    private const float SafeAcceleration = 8f;

    private static readonly FieldInfo LandingField =
        AccessTools.Field(typeof(AirbaseOverlay), "landing");

    private static readonly FieldInfo EngineOperableField =
        AccessTools.Field(typeof(TurbineEngine), "operable");

    private static readonly FieldInfo FlightHudCanvasField =
        AccessTools.Field(typeof(FlightHud), "canvas");

    private static readonly FieldInfo PilotHitPointsField =
        AccessTools.Field(typeof(Pilot), "hitPoints");

    private static readonly FieldInfo PilotVelocityPrevField =
        AccessTools.Field(typeof(Pilot), "velocityPrev");

    /// <summary>
    /// Tell every pilot on this aircraft that it has always been travelling at
    /// this velocity, so the step in which the harness changed it does not read
    /// as acceleration.
    ///
    /// <para>This is what was killing the pilot. <c>Pilot</c>'s fixed-step job
    /// computes <c>accel = (rb.velocity - velocityPrev) / (fixedDeltaTime *
    /// 9.81)</c> and calls <c>TakeGForceDamage</c> above 20 g. A teleport sets
    /// velocity discontinuously, which is by definition infinite acceleration:
    /// going from parked to 93 m/s in one 0.02 s step reads as roughly 470 g,
    /// and the damage term is quadratic in it. Hit points went from 100 to
    /// -65404 in a single step, and every frame afterwards was a free camera
    /// outside a dead pilot's aeroplane with the flight HUD switched off.</para>
    ///
    /// <para>Only the placement and the hold do this, and only to the aircraft
    /// they are already teleporting. A real change of velocity still hurts.</para>
    /// </summary>
    private static void ForgetAcceleration(Aircraft aircraft, Vector3 velocity)
    {
        if (PilotVelocityPrevField == null || aircraft.pilots == null) return;

        foreach (var pilot in aircraft.pilots)
        {
            if (pilot == null) continue;
            PilotVelocityPrevField.SetValue(pilot, velocity);
        }
    }

    /// <summary>True while Auto Approach is holding an aircraft in place.</summary>
    public static bool HoldingApproach { get; private set; }

    /// <summary>
    /// Suppress G-force damage while the harness is holding an approach.
    ///
    /// <para>Two gentler attempts failed and are worth recording. Zeroing the
    /// pilot's remembered velocity got the damage from 65504 to 2486 in one run
    /// and not at all in the next, because whether it helps depends on whether
    /// the harness's FixedUpdate ran before or after <c>Pilot</c>'s fixed-step
    /// job. Capping the per-step velocity change at 8 g did not help either:
    /// the damage arrives 0.04 s after the placement, saturated at 65504 —
    /// which is the largest half-precision float, so the acceleration being
    /// measured is not merely large, it has overflowed. A teleport is a
    /// discontinuity in position as well as velocity, and nothing that samples
    /// a derivative across it will return a finite answer.</para>
    ///
    /// <para>So the discontinuity is not made survivable, it is excused. The
    /// harness is not testing the G model; it is putting the aircraft where the
    /// landing symbology draws. This is on only while a hold is in effect, and
    /// a hold only happens under Auto Approach.</para>
    /// </summary>
    [HarmonyPatch(typeof(Pilot), nameof(Pilot.TakeGForceDamage))]
    private static class SuppressHoldGForceDamage
    {
        [HarmonyPrefix]
        private static bool Prefix() => !HoldingApproach;
    }

    private bool _approachPlaced;
    private bool _approachSettled;
    private float _approachDeadline;
    private Airbase.Runway.RunwayUsage? _approachUsage;
    private AirbaseOverlay _airbaseOverlay;
    private Aircraft _placedAircraft;
    private float _hudBoundAt;
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

        // Re-place when the aircraft is a different one. A mission that spawns
        // its own airframe (the tutorials do, once their briefing is answered)
        // replaces the one the harness spawned, and a run pinned to a
        // destroyed aircraft holds nothing.
        if (!_approachPlaced || !ReferenceEquals(aircraft, _placedAircraft))
        {
            if (!SettledInCockpit(aircraft)) return false;

            if (!PlaceOnApproach(aircraft)) return false;
            _placedAircraft = aircraft;
            _approachPlaced = true;
            _approachDeadline = Time.unscaledTime + ApproachTimeout;
        }

        HoldOnApproach(aircraft);
        WatchPilot(aircraft);

        if (_approachSettled) return true;

        if (IsLanding())
        {
            _approachSettled = true;
            _nextDumpAt = Time.unscaledTime + ModConfiguration.Instance.AutoDumpDelay.Value;
            Debug.Log(
                "[NOVR-HARNESS] Approach accepted — the game is drawing the landing symbology. " +
                $"First dump in {ModConfiguration.Instance.AutoDumpDelay.Value:0.#}s. " +
                DescribeAirframe(aircraft));
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

    /// <summary>
    /// True once the game has finished putting the player in this cockpit and
    /// had a moment to settle.
    ///
    /// <para>Measured, and the reason this gate exists: teleporting on the
    /// first frame <c>GetLocalAircraft</c> succeeds — which is during the spawn
    /// sequence, not after it — leaves <c>CombatHUD.aircraft</c> null and the
    /// whole HUD canvas deactivated for the rest of the run. Same run without
    /// the teleport: bound at level time 2.1 s with the airbase overlay live
    /// and its takeoff timer counting. Owning an aircraft and being seated in
    /// it are not the same event, and only the second one is safe to move.</para>
    /// </summary>
    private bool SettledInCockpit(Aircraft aircraft)
    {
        var combatHud = SceneSingleton<CombatHUD>.i;
        if (combatHud == null || combatHud.aircraft != aircraft)
        {
            _hudBoundAt = 0f;
            return NotApproaching("the flight HUD has not been given this aircraft yet");
        }

        if (_hudBoundAt <= 0f) _hudBoundAt = Time.unscaledTime;
        if (Time.unscaledTime < _hudBoundAt + CockpitSettleSeconds) return false;

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

            // Before, so a run that arrives at the hold point already broken is
            // distinguishable from one the placement breaks. The first version
            // of this mode produced dead engines and there was no way to tell
            // which end of the teleport did it.
            Debug.Log($"[NOVR-HARNESS] Airframe before placement: {DescribeAirframe(aircraft)}");

            var position = ApproachPosition(aircraft);
            var rotation = ApproachRotation();

            // Through the rigidbody, and at rest.
            //
            // Writing transform.position alone was killing the pilot outright:
            // hit points went from 100 to -65404 in 0.03 s, one physics step
            // after the placement. Physics.autoSyncTransforms is off, so a
            // transform write does not reach the body until the next
            // FixedUpdate — and the approach velocity was being set in the same
            // breath. For one step the body was still parked in the hangar,
            // doing 93 m/s. It hit the hangar, AeroPart's impact term divided
            // the impulse by the fixed timestep, and the pilot was dead before
            // the aircraft had visibly moved. Since
            // CameraCockpitState.UpdateState leaves for the free camera on
            // pilot.dead, and every state but the cockpit disables the flight
            // HUD, every frame after that was a free camera outside the
            // aeroplane with no HUD on it.
            //
            // rb.position/rb.rotation move the body immediately, so there is no
            // step in which its pose and its velocity disagree. The approach
            // velocity is left to the FixedUpdate hold, by which time the body
            // is out in clear air.
            if (aircraft.rb != null)
            {
                aircraft.rb.position = position;
                aircraft.rb.rotation = rotation;
                aircraft.rb.velocity = Vector3.zero;
                aircraft.rb.angularVelocity = Vector3.zero;
            }

            // Before the teleport, not after: the discontinuity is the thing
            // being excused, and Pilot's job may run between this frame's
            // Update and the next FixedUpdate.
            HoldingApproach = true;
            _holdVelocity = Vector3.zero;
            ForgetAcceleration(aircraft, Vector3.zero);
            aircraft.transform.SetPositionAndRotation(position, rotation);
            Physics.SyncTransforms();

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
                $"{ApproachDistance:0} m out, gear down, " +
                $"{ApproachVelocity(query.LandingSpeed).magnitude:0} m/s once the hold takes over, " +
                $"{TerrainClearance(position):0} m above the terrain under it " +
                $"(lowest clearance along the final: {LowestClearanceOnFinal(aircraft):0} m).");
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
        var geometric = threshold - usage.GetDirection().normalized * ApproachDistance + Vector3.up * height;

        // Raised to clear the ground if the geometric glideslope point is not
        // above it. This is not a nicety. The glideslope is drawn relative to
        // the runway's own elevation, so at an airbase in a valley — which
        // airbase_desert1 is — a geometrically correct 1200 m final passes
        // through the hillside short of the threshold. The aircraft was being
        // teleported into that hillside, every frame: AeroPart.OnCollisionEnter
        // turns the impulse into impact damage, TurbineEngine.KillEngine fires
        // once a part's condition reaches zero, and the run captured a
        // dead-stick glider with both engines INOPERABLE descending at
        // 4600 fpm. The frame looked like an approach and was a crash.
        var clearance = TerrainClearance(geometric);
        if (clearance >= MinTerrainClearance) return geometric;

        return geometric + Vector3.up * (MinTerrainClearance - clearance);
    }

    /// <summary>
    /// Height of a point above whatever the game considers ground beneath it,
    /// using the same layer mask <c>Aircraft.CheckRadarAlt</c> uses so this
    /// agrees with the <c>radarAlt</c> the overlay's own test reads.
    /// Returns <see cref="float.PositiveInfinity"/> over nothing at all.
    /// </summary>
    private static float TerrainClearance(Vector3 point)
    {
        var mask = (int)PhysicsLayers.StaticsMask | (int)PhysicsLayers.ShipsMask;
        return Physics.Linecast(point, point - Vector3.up * 10000f, out var hit, mask)
            ? hit.distance
            : float.PositiveInfinity;
    }

    /// <summary>
    /// The worst clearance along the final, sampled every 100 m, ignoring the
    /// last 200 m before the threshold.
    ///
    /// <para>The exclusion is the whole point of the number. A glideslope ends
    /// on the runway, so sampling all the way in always reports a clearance near
    /// zero and the metric says "the approach path is in the ground" on every
    /// approach ever flown — a measurement that is alarming, constant, and
    /// therefore worthless. What is worth knowing is whether the path clips
    /// something on the way in.</para>
    /// </summary>
    private float LowestClearanceOnFinal(Aircraft aircraft)
    {
        const float ignoreNearThreshold = 200f;

        var threshold = _approachUsage.Value.GetStart().position;
        var hold = ApproachPosition(aircraft);
        var distance = ApproachDistance;
        if (distance <= ignoreNearThreshold) return TerrainClearance(hold);

        var steps = Mathf.Max(1, Mathf.CeilToInt(distance / 100f));
        var lowest = float.PositiveInfinity;
        for (var step = 0; step <= steps; step++)
        {
            var along = step / (float)steps;
            if (along * distance < ignoreNearThreshold) continue;
            lowest = Mathf.Min(lowest, TerrainClearance(Vector3.Lerp(threshold, hold, along)));
        }

        return lowest;
    }

    private Quaternion ApproachRotation() =>
        Quaternion.LookRotation(_approachUsage.Value.GetDirection().normalized, Vector3.up);

    private Vector3 ApproachVelocity(float landingSpeed) =>
        _approachUsage.Value.GetDirection().normalized * Mathf.Max(60f, landingSpeed * 1.3f);

    /// <summary>
    /// Keep the aircraft on the hold point, from <c>FixedUpdate</c> and through
    /// the rigidbody.
    ///
    /// <para>It used to do this from <c>Update</c> by writing
    /// <c>transform.position</c>. That writes a pose the physics step then
    /// integrates away from and the next write teleports back — a sawtooth the
    /// solver sees as motion, and every contact it produces is an impulse
    /// divided by <c>Time.fixedDeltaTime</c> in
    /// <c>AeroPart.OnCollisionEnter</c>'s damage term. Correcting inside the
    /// physics step instead means the pose the solver starts from is the pose
    /// we asked for, so there is nothing for it to resolve.</para>
    /// </summary>
    private void HoldOnApproach(Aircraft aircraft)
    {
        if (_approachUsage == null) return;

        var position = ApproachPosition(aircraft);
        var rotation = ApproachRotation();

        if (aircraft.rb != null)
        {
            aircraft.rb.position = position;
            aircraft.rb.rotation = rotation;

            // Approached at a survivable rate rather than snapped to.
            //
            // Zeroing the pilot's remembered velocity was the obvious fix and
            // it only got the damage down from 65504 to 2486: whether it helps
            // depends on whether this FixedUpdate runs before or after Pilot's
            // own fixed-step job, and script order is not ours to choose. A
            // per-step cap needs no such luck — the acceleration the pilot's
            // job measures is bounded by construction, whichever of us runs
            // first. Half a second of ramp, and the aircraft is held from then
            // on at a velocity that never changes again.
            // Ramped from what the hold last commanded, not from what the
            // rigidbody currently reads. Reading the body back put the airspeed
            // at 3683 m/s: teleporting the body every step leaves it with a
            // velocity that has nothing to do with flight, and a ramp that
            // starts there spends forty seconds converging on the number it was
            // supposed to hold. The hold owns this value outright.
            var target = ApproachVelocity(aircraft.GetAircraftParameters().takeoffSpeed);
            var maxDelta = SafeAcceleration * Time.fixedDeltaTime * 9.81f;
            var velocity = Vector3.MoveTowards(_holdVelocity, target, maxDelta);
            _holdVelocity = velocity;

            aircraft.rb.velocity = velocity;
            aircraft.rb.angularVelocity = Vector3.zero;
            ForgetAcceleration(aircraft, velocity);
        }

        aircraft.transform.SetPositionAndRotation(position, rotation);

        if (!aircraft.gearDeployed) aircraft.SetGear(deployed: true);
    }

    private void FixedUpdate()
    {
        if (!ApproachRequested || !_approachPlaced) return;
        if (!GameManager.GetLocalAircraft(out var aircraft) || aircraft == null) return;
        if (!ReferenceEquals(aircraft, _placedAircraft)) return;

        HoldOnApproach(aircraft);
    }

    /// <summary>
    /// Whether every engine on the aircraft is still working, and the reason if
    /// not.
    ///
    /// <para>Reported at dump time because the harness has already once handed
    /// over a frame of a crash as if it were a frame of an approach — both
    /// engines dead, 4600 fpm down — and nothing in the dump said so. An engine
    /// only reads INOPERABLE after <c>TurbineEngine.KillEngine</c>, which only
    /// runs from damage, so this doubles as the detector for a placement that is
    /// putting the aircraft through scenery.</para>
    /// </summary>
    private static string DescribeAirframe(Aircraft aircraft)
    {
        try
        {
            var engines = aircraft.GetComponentsInChildren<TurbineEngine>(true);
            var dead = 0;
            var thrust = 0f;
            foreach (var engine in engines)
            {
                if (engine == null) continue;
                thrust += engine.GetThrust();
                if (EngineOperableField?.GetValue(engine) is bool operable && !operable) dead++;
            }

            var health = dead == 0
                ? $"engines={engines.Length} all operable"
                : $"engines={engines.Length} INOPERABLE={dead} — this aircraft is damaged, " +
                  "the frames are of a crash and not of an approach";

            return $"{health} ignition={aircraft.Ignition} thrust={thrust:0} " +
                   $"speed={(aircraft.rb != null ? aircraft.rb.velocity.magnitude : 0f):0} m/s " +
                   $"radarAlt={aircraft.radarAlt:0} m {DescribePilot(aircraft)} {DescribeView()}";
        }
        catch (Exception e)
        {
            return $"(could not describe the airframe: {e.Message})";
        }
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

    private static readonly FieldInfo DialogueTitleField =
        AccessTools.Field(typeof(DialogueBox), "titleText");

    private static string DialogueTitle(DialogueBox box)
    {
        try
        {
            var component = DialogueTitleField?.GetValue(box) as Component;
            var text = component != null ? component.GetComponent<TMPro.TMP_Text>() : null;
            return text != null ? text.text : "<unreadable>";
        }
        catch (Exception)
        {
            return "<unreadable>";
        }
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

    /// <summary>
    /// The pilot's condition. <c>CameraCockpitState.UpdateState</c> switches to
    /// the free camera the moment <c>pilot.dead</c> goes true, and every camera
    /// state but the cockpit calls <c>FlightHud.EnableCanvas(false)</c> — so a
    /// dead pilot silently ends the run's ability to see any HUD at all, and
    /// hit points falling is the earliest warning of it.
    /// </summary>
    private static string DescribePilot(Aircraft aircraft)
    {
        var pilot = aircraft.pilots != null && aircraft.pilots.Length > 0 ? aircraft.pilots[0] : null;
        if (pilot == null) return "pilot=<none>";

        var hp = PilotHitPointsField?.GetValue(pilot) is float f ? f : float.NaN;
        return $"pilot={(pilot.dead ? "DEAD" : "alive")} hp={hp:0} ejected={pilot.ejected}";
    }

    /// <summary>
    /// Log the moment the pilot's condition changes, rather than only at the
    /// dump. "The pilot was dead by the time we captured" and "the placement
    /// killed the pilot" are different bugs, and only a timestamp separates
    /// them.
    /// </summary>
    private void WatchPilot(Aircraft aircraft)
    {
        var pilot = aircraft.pilots != null && aircraft.pilots.Length > 0 ? aircraft.pilots[0] : null;
        if (pilot == null) return;

        var hp = PilotHitPointsField?.GetValue(pilot) is float f ? f : float.NaN;
        if (Mathf.Approximately(hp, _lastPilotHitPoints) && pilot.dead == _lastPilotDead) return;

        _lastPilotHitPoints = hp;
        _lastPilotDead = pilot.dead;
        Debug.LogWarning($"[NOVR-HARNESS] Pilot condition changed at {Time.timeSinceLevelLoad:0.00}s: " +
                         $"{DescribePilot(aircraft)} {DescribeView()}");
    }

    private Vector3 _holdVelocity;
    private float _lastPilotHitPoints = float.NaN;
    private bool _lastPilotDead;

    /// <summary>
    /// The camera state, and whether the flight HUD canvas is switched on.
    ///
    /// <para>These belong together because one drives the other:
    /// <c>FlightHud.EnableCanvas(false)</c> is called by the entry of every
    /// camera state that is not the cockpit — relative, controlled, TV,
    /// selection, and the map. So a HUD canvas that is off is almost never a
    /// HUD problem; it is the view having left the cockpit, and every symbol
    /// the run was capturing goes with it.</para>
    /// </summary>
    private static string DescribeView()
    {
        try
        {
            var manager = SceneSingleton<CameraStateManager>.i;
            var state = manager != null && manager.currentState != null
                ? manager.currentState.GetType().Name
                : "<no camera state>";

            var hud = SceneSingleton<FlightHud>.i;
            var hudCanvas = hud != null ? FlightHudCanvasField?.GetValue(hud) as Canvas : null;
            var canvas = hudCanvas != null
                ? $"hudCanvas={(hudCanvas.gameObject.activeInHierarchy ? "on" : "OFF")}"
                : "hudCanvas=<none>";

            return $"view={state} {canvas}";
        }
        catch (Exception e)
        {
            return $"(could not describe the view: {e.Message})";
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
            int airbases = 0, owned = 0, offered = 0, noHangar = 0, notOwned = 0, wrongType = 0;

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
                    if (!WantedAircraft(definition)) { wrongType++; continue; }
                    if (!player.OwnsAirframe(definition, includeReserved: true)) { notOwned++; continue; }

                    spawner
                        .RequestSpawnAtAirbase(airbase, definition, default, new Loadout(), 1f)
                        .Forget();
                    Debug.Log($"[NOVR-HARNESS] Requested spawn: {definition.unitName} at {airbase.name}.");
                    return true;
                }
            }

            // Every airbase has a hangar and every aircraft is offered, and the
            // player simply owns none of them: that is Free Flight, where the
            // airframes are handed out by the selection UI rather than by the
            // mission. Credit one and let the next tick spawn it.
            if (offered > 0 && noHangar == 0 && notOwned > 0 && notOwned + wrongType == offered &&
                TryCreditAirframe(player))
            {
                return false;
            }

            return NotReady(
                $"no spawnable aircraft (airbases={airbases} ours={owned} " +
                $"offered={offered} noHangar={noHangar} wrongType={wrongType} notOwned={notOwned}). " +
                DescribeOffered(player));
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
    /// Whether this is the aircraft the run asked for.
    ///
    /// <para>Taking whatever the first airbase offered was fine while the only
    /// question was "is there a cockpit", and stopped being fine the moment the
    /// harness started testing the flight HUD. Free Flight's first offer at
    /// airbase_city is a CI-22 Cricket, a light aircraft with no HUD at all:
    /// the run reached a cockpit, held a textbook approach, and dumped a frame
    /// with <c>HUDCanvas</c> inactive — an empty result that looks exactly like
    /// a broken HUD.</para>
    /// </summary>
    private static bool WantedAircraft(AircraftDefinition definition)
    {
        var wanted = ModConfiguration.Instance.AutoSpawnAircraft.Value;
        if (string.IsNullOrWhiteSpace(wanted)) return true;

        return definition.unitName != null &&
               definition.unitName.IndexOf(wanted, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    /// <summary>
    /// Every aircraft any of our airbases will offer, so a run that asked for
    /// one by a name nothing matches says what it could have had instead of
    /// timing out with a count.
    /// </summary>
    private static string DescribeOffered(Player player)
    {
        try
        {
            var names = new List<string>();
            foreach (var airbase in player.HQ.GetAirbases())
            {
                if (airbase == null || airbase.CurrentHQ != player.HQ) continue;
                foreach (var definition in airbase.GetAvailableAircraft())
                {
                    if (definition?.unitName == null) continue;
                    if (!names.Contains(definition.unitName)) names.Add(definition.unitName);
                }
            }

            return names.Count == 0 ? "nothing offered anywhere." : "offered: " + string.Join(", ", names) + ".";
        }
        catch (Exception e)
        {
            return $"(could not list the offered aircraft: {e.Message})";
        }
    }

    /// <summary>
    /// Give the player one airframe so there is something to spawn.
    ///
    /// <para>This is what makes Free Flight usable, and Free Flight is the only
    /// mission with nothing to fight: no script that replaces the aircraft, no
    /// area bounds that read a teleport as desertion, no patience to run out
    /// of. The harness had it written off as "no airbase hangar spawn", which
    /// was the wrong reason — the count says <c>noHangar=0 notOwned=56</c>, so
    /// every hangar was willing and the player just owned nothing. Free Flight
    /// hands out airframes through the selection UI the harness deliberately
    /// does not drive.</para>
    ///
    /// <para><c>Spawner.AllowedToSpawn</c> enforces ownership server-side, so
    /// there is no filter to relax on our side; the airframe has to actually
    /// exist. <c>CreditAirframe</c> is the game's own [Server] method for
    /// granting one, and in single player the local player is the server.
    /// Logged loudly because it changes what the game would otherwise
    /// permit.</para>
    /// </summary>
    private bool TryCreditAirframe(Player player)
    {
        if (_creditedAirframe) return false;

        try
        {
            foreach (var airbase in player.HQ.GetAirbases())
            {
                if (airbase == null || airbase.CurrentHQ != player.HQ) continue;

                foreach (var definition in airbase.GetAvailableAircraft())
                {
                    if (definition == null) continue;
                    if (!airbase.CanSpawnAircraft(definition)) continue;
                    if (!WantedAircraft(definition)) continue;

                    player.CreditAirframe(definition, 1, reserved: false);
                    _creditedAirframe = true;
                    Debug.Log(
                        $"[NOVR-HARNESS] Nothing was owned, so credited one {definition.unitName} " +
                        $"at {airbase.name}. This is the harness granting itself an airframe the " +
                        "mission did not; it is how Free Flight is reachable at all.");
                    return true;
                }
            }
        }
        catch (Exception e)
        {
            // Throws if we are not the server, which means this is not a
            // single-player host and crediting was never ours to do.
            Debug.Log($"[NOVR-HARNESS] Could not credit an airframe: {e.Message}");
        }

        _creditedAirframe = true;
        return false;
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

        // What the aeroplane was actually doing in the frame that was just
        // captured. Without it a dump has to be read backwards out of its own
        // pixels, which is how a crash got reported as an approach.
        if (ApproachRequested && GameManager.GetLocalAircraft(out var dumped) && dumped != null)
        {
            Debug.Log($"[NOVR-HARNESS] Airframe at dump {index}: {DescribeAirframe(dumped)}");
        }

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
