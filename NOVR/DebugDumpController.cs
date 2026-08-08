using System;
using System.IO;
using NOVR.VrCamera;
using UnityEngine;

namespace NOVR;

// F1 (or a "dump.trigger" file next to NOVR.dll) fires one frame's buffer
// dump via VrDebugDump. The trigger file lets an external process (the WSL
// dev session) request a dump without focusing the game window.
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
        if (_dumpKey.UpdateIsDown() || ConsumeDumpTriggerFile())
        {
            VrDebugDump.Request();
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
