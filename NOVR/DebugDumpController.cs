using System;
using System.IO;
using NOVR.VrCamera;
using UnityEngine;

namespace NOVR;

// F1 (or a "dump.trigger" file next to NOVR.dll) fires one frame's buffer
// dump via VrDebugDump. The trigger file lets an external process (the WSL
// dev session) request a dump without focusing the game window.
//
// The same trigger also fires a RenderDoc frame capture when RenderDoc is
// injected, so one keypress produces both halves of the picture: the mod's own
// C# state (VrDebugDump) and the GPU's view of the frame (RenderDoc). Outside
// the harness RenderDoc is absent and that half is silently skipped.
//
// Both entry points are behind [Debug] Enable Frame Dumps, off by default. A
// dump stalls the frame and writes several megabytes of PNGs, so claiming F1 in
// a shipped mod would mean a player who pressed it while flying got an
// unexplained hitch. Auto Start Mission does not come through here — it calls
// VrDebugDump directly — so the harness is unaffected by the gate.
public class DebugDumpController : NOVRBehaviour
{
    private readonly KeyboardKey _dumpKey = new(KeyboardKey.KeyCode.F1);
    private float _nextTriggerCheck;

    protected override void Awake()
    {
        base.Awake();
        VrDebugDump.Initialize();
    }

    private void Update()
    {
        // Enforced every frame rather than set once: the game writes the
        // player's volume settings back on its own schedule, and a mute that
        // can be un-muted mid-run is not a mute.
        if (ModConfiguration.Instance.HarnessMute.Value && AudioListener.volume != 0f)
        {
            AudioListener.volume = 0f;
        }

        if (ModConfiguration.Instance.EnableFrameDumps.Value &&
            (_dumpKey.UpdateIsDown() || ConsumeDumpTriggerFile()))
        {
            VrDebugDump.Request();
            if (ModConfiguration.Instance.RenderDocCaptureOnDump.Value)
            {
                RenderDocCapture.TryTriggerCapture();
            }
        }

        VrDebugDump.Tick();
    }

    // Polled ~once a second (mirrors Spvr.UuvrCore).
    private bool ConsumeDumpTriggerFile()
    {
        if (Time.unscaledTime < _nextTriggerCheck) return false;
        _nextTriggerCheck = Time.unscaledTime + 1f;

        try
        {
            var path = Path.Combine(NOVRPlugin.ModFolderPath, "dump.trigger");
            if (!File.Exists(path)) return false;
            File.Delete(path);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
