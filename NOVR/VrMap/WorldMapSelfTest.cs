using System.Collections.Generic;
using UnityEngine;

namespace NOVR.VrMap;

/// <summary>
/// Flies the map for you, so the two questions a screenshot cannot answer get
/// answered: what the map costs, and whether closing it puts everything back.
///
/// <para>Both are otherwise out of reach from the harness, which sets the config
/// once and launches — it can open the map for a whole run or leave it shut for a
/// whole run, and never watches the moment in between. So this opens and closes
/// it on a timer and reports each phase, which turns "no frame time" and "the
/// restore path has never been watched" into one run.</para>
///
/// <para>The first frames of each phase are thrown away. Opening the map builds
/// the model on the frame it is first asked for, and a build spike averaged in
/// with steady state is a number that describes neither.</para>
///
/// <para>What it measures is the game's own frame time under a mock runtime with
/// no compositor — the cost of drawing the map, not the rate a headset is
/// handed frames at. Useful for "what does this feature cost"; not a substitute
/// for flying it.</para>
/// </summary>
internal sealed class WorldMapSelfTest
{
    private const float PhaseSeconds = 8f;
    private const int SettleFrames = 45;

    private readonly List<float> _samples = new();
    private float _phaseStarted;
    private int _framesInPhase;
    private bool _phaseOpen;
    private bool _running;

    /// <summary>
    /// Called every frame while the self test is on. Returns true on the frame it
    /// closes the map, which is the frame the restore check wants.
    /// </summary>
    public void Tick(bool open)
    {
        if (!_running)
        {
            Begin(open);
            return;
        }

        if (open != _phaseOpen)
        {
            // Someone (the shortcut) changed it under us. Report what we have and
            // start again on the new state rather than mixing the two.
            Report();
            Begin(open);
            return;
        }

        _framesInPhase++;
        if (_framesInPhase > SettleFrames) _samples.Add(Time.unscaledDeltaTime);

        if (Time.unscaledTime - _phaseStarted < PhaseSeconds) return;

        Report();
        if (VrMapConfig.Open != null) VrMapConfig.Open.Value = !open;
        Begin(!open);
    }

    private void Begin(bool open)
    {
        _running = true;
        _phaseOpen = open;
        _phaseStarted = Time.unscaledTime;
        _framesInPhase = 0;
        _samples.Clear();
    }

    private void Report()
    {
        if (_samples.Count < 30)
        {
            Debug.Log($"[NOVR] World map self test: {(_phaseOpen ? "open" : "closed")} phase too " +
                      $"short to time ({_samples.Count} sample(s)).");
            return;
        }

        _samples.Sort();
        var total = 0f;
        foreach (var sample in _samples) total += sample;

        var mean = total / _samples.Count;
        var median = _samples[_samples.Count / 2];
        var p95 = _samples[Mathf.Min(_samples.Count - 1, (int)(_samples.Count * 0.95f))];
        var worst = _samples[_samples.Count - 1];

        Debug.Log(
            $"[NOVR] World map frame time, map {(_phaseOpen ? "OPEN" : "CLOSED")}: " +
            $"{_samples.Count} frames, mean {mean * 1000f:F2} ms ({1f / mean:F0} fps), " +
            $"median {median * 1000f:F2} ms, p95 {p95 * 1000f:F2} ms, worst {worst * 1000f:F2} ms.");
    }
}
