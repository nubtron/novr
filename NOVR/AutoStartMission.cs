using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Cysharp.Threading.Tasks;
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

    private void Awake()
    {
        if (!ModConfiguration.Instance.AutoStartMission.Value)
        {
            enabled = false;
            return;
        }

        _dumpsRemaining = ModConfiguration.Instance.AutoDumpCount.Value;
        ClearDoneMarker();
        Debug.Log("[NOVR-HARNESS] Auto Start Mission enabled — the game will start a mission and dump unattended.");
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
            _nextDumpAt = Time.unscaledTime + ModConfiguration.Instance.AutoDumpDelay.Value;
            Debug.Log($"[NOVR-HARNESS] Mission running, local aircraft acquired. First dump in {ModConfiguration.Instance.AutoDumpDelay.Value:0.#}s.");
            return;
        }

        if (Time.unscaledTime < _nextDumpAt) return;

        FireDump();
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
            if (!GameManager.GetLocalPlayer<Player>(out var player) || player == null) return false;
            if (player.HQ == null) return false;

            var spawner = NetworkSceneSingleton<Spawner>.i;
            if (spawner == null) return false;

            foreach (var airbase in player.HQ.GetAirbases())
            {
                if (airbase == null || airbase.CurrentHQ != player.HQ) continue;

                foreach (var definition in airbase.GetAvailableAircraft())
                {
                    if (definition == null) continue;
                    if (!airbase.CanSpawnAircraft(definition)) continue;
                    if (!player.OwnsAirframe(definition, includeReserved: true)) continue;

                    spawner
                        .RequestSpawnAtAirbase(airbase, definition, default, new Loadout(), 1f)
                        .Forget();
                    Debug.Log($"[NOVR-HARNESS] Requested spawn: {definition.unitName} at {airbase.name}.");
                    return true;
                }
            }

            Debug.Log("[NOVR-HARNESS] No spawnable aircraft at any friendly airbase yet; retrying.");
            return false;
        }
        catch (Exception e)
        {
            // The mission scene is still assembling for a while after the host
            // starts; treat anything thrown here as "not ready".
            Debug.Log($"[NOVR-HARNESS] Spawn attempt failed (will retry): {e.Message}");
            return false;
        }
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

        // Free Flight (MissionGroup.Default) puts the player in the air with
        // the HUD live in a few seconds and nothing shooting at them — the
        // cheapest reproducible frame for HUD work.
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
        var index = ModConfiguration.Instance.AutoDumpCount.Value - _dumpsRemaining + 1;
        VrDebugDump.Request($"auto-{index}");
        if (ModConfiguration.Instance.RenderDocCaptureOnDump.Value)
        {
            RenderDocCapture.TryTriggerCapture();
        }

        _dumpsRemaining--;
        Debug.Log($"[NOVR-HARNESS] Fired dump {index}/{ModConfiguration.Instance.AutoDumpCount.Value}.");

        if (_dumpsRemaining > 0)
        {
            _nextDumpAt = Time.unscaledTime + ModConfiguration.Instance.AutoDumpDelay.Value;
            return;
        }

        Finish();
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
            File.WriteAllText(path, $"dumps={ModConfiguration.Instance.AutoDumpCount.Value}\nrenderdoc={RenderDocCapture.IsAvailable}\n");
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
