using System;
using System.Runtime.InteropServices;
using UnityEngine;

namespace NOVR;

/// <summary>
/// In-app RenderDoc capture trigger (renderdoc_app.h ported by hand — there is
/// no official managed binding).
///
/// This complements <see cref="NOVR.VrCamera.VrDebugDump"/> rather than
/// replacing it. The buffer dump records what the mod's own C# sees — per-eye
/// stereo matrices, which canvases were translated to world space, which camera
/// owns which target texture — none of which a GPU capture knows about. A
/// RenderDoc capture records what the GPU was actually told to do: per-draw
/// blend state, the real bound textures with their alpha channels, and pixel
/// history for a single HUD pixel. For the HUD transparency work specifically
/// that is the difference between the CPU-side "does this texture have usable
/// alpha" heuristic in NOVRFlightHudBehavior and ground truth.
///
/// Requires renderdoc.dll to already be in the process. The harness injects it
/// (tools/capture.py — Steam launch, then `renderdoccmd inject --PID` before
/// the D3D device is created); on a normal play session nothing is injected and
/// every method here is a silent no-op.
/// </summary>
public static class RenderDocCapture
{
    // RENDERDOC_API_1_x_x is a single struct that only ever grows by appending
    // fields, so TriggerCapture sits at the same offset — the 16th pointer,
    // index 15 — no matter which version enum is requested. See the typedef
    // chain in renderdoc_app.h.
    private const int TriggerCaptureFieldIndex = 15;
    private const int RENDERDOC_API_Version_1_1_2 = 10102;

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string lpModuleName);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern IntPtr GetProcAddress(IntPtr hModule, string procName);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GetApiDelegate(int version, out IntPtr outApiPointers);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void TriggerCaptureDelegate();

    private static TriggerCaptureDelegate _triggerCapture;
    private static bool _initAttempted;

    /// <summary>True when renderdoc.dll was found and the API resolved.</summary>
    public static bool IsAvailable
    {
        get
        {
            if (!_initAttempted) Init();
            return _triggerCapture != null;
        }
    }

    /// <summary>
    /// Ask RenderDoc to capture the next frame. Returns false when RenderDoc is
    /// not injected, which is the normal case outside the harness.
    /// </summary>
    public static bool TryTriggerCapture()
    {
        if (!IsAvailable) return false;

        try
        {
            _triggerCapture();
            Debug.Log("[NOVR-DUMP] RenderDoc capture triggered for the next frame.");
            return true;
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[NOVR-DUMP] RenderDoc TriggerCapture failed: {e}");
            return false;
        }
    }

    private static void Init()
    {
        _initAttempted = true;
        try
        {
            var module = GetModuleHandle("renderdoc.dll");
            if (module == IntPtr.Zero)
            {
                Debug.Log("[NOVR-DUMP] renderdoc.dll not in this process — GPU capture unavailable (inject with renderdoccmd first).");
                return;
            }

            var getApiPtr = GetProcAddress(module, "RENDERDOC_GetAPI");
            if (getApiPtr == IntPtr.Zero)
            {
                Debug.LogWarning("[NOVR-DUMP] renderdoc.dll is loaded but exports no RENDERDOC_GetAPI.");
                return;
            }

            var getApi = (GetApiDelegate)Marshal.GetDelegateForFunctionPointer(getApiPtr, typeof(GetApiDelegate));
            var result = getApi(RENDERDOC_API_Version_1_1_2, out var apiPointers);
            if (result != 1 || apiPointers == IntPtr.Zero)
            {
                Debug.LogWarning($"[NOVR-DUMP] RENDERDOC_GetAPI failed (result={result}).");
                return;
            }

            var triggerCapturePtr = Marshal.ReadIntPtr(apiPointers, TriggerCaptureFieldIndex * IntPtr.Size);
            if (triggerCapturePtr == IntPtr.Zero)
            {
                Debug.LogWarning("[NOVR-DUMP] RenderDoc API TriggerCapture pointer is null.");
                return;
            }

            _triggerCapture = (TriggerCaptureDelegate)Marshal.GetDelegateForFunctionPointer(
                triggerCapturePtr, typeof(TriggerCaptureDelegate));
            Debug.Log("[NOVR-DUMP] RenderDoc detected — buffer dumps will also trigger a GPU frame capture.");
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[NOVR-DUMP] RenderDoc init failed: {e}");
        }
    }
}
