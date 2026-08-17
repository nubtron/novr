using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using NOVR.VrUi;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using UnityEngine.XR;

namespace NOVR.VrCamera;

// One keypress (F1) or a "dump.trigger" file next to NOVR.dll dumps ground
// truth for one frame:
//   - per-camera eye-target grabs, executed from URP's endCameraRendering via
//     context.ExecuteCommandBuffer(Blit CameraTarget -> RT) + async GPU
//     readback, on the tracked main camera ("eye_main", the scene) and NOVR's
//     VR HUD overlay camera ("eye_hudOverlay", the translated world-space UI
//     layer). The overlay renders after the base camera in the URP stack, so
//     the pair brackets "is the HUD missing / misplaced".
//   - every game RenderTexture (any camera with a targetTexture set, e.g. the
//     in-cockpit TargetCam screens), grabbed right after that camera rendered
//   - a SideBySide mirror screenshot of the frame (the desktop composite)
//   - meta.txt + meta.json with the camera rig (per-eye stereo matrices),
//     canvas inventory (which HUD canvases NOVR translated to world space),
//     URP shared-texture globals, and the active mod config
// Files land in BepInEx/plugins/NOVR/dumps/<time>/ — readable from WSL
// without touching the game. See tools/dump-viewer in novr-research for the
// HTML gallery that renders these dumps.
//
// Relationship to RenderDoc: complementary, and both now fire from the same
// trigger (see RenderDocCapture + DebugDumpController). This dump records
// C#-side state a GPU capture cannot see — per-eye stereo matrices,
// targetTexture identity, which canvases NOVR moved to world space. RenderDoc
// records what the GPU was actually told to do — per-draw blend state, the real
// bound textures with their alpha channels, pixel history. An earlier version
// of this comment said RenderDoc "cannot be fired unattended from a headset
// session"; that no longer holds. tools/capture.py runs the whole thing with no
// headset and nobody at the keyboard: the OpenXR mock runtime supplies stereo,
// AutoStartMission flies the mission, and RenderDoc is injected at launch.
//
// Engine notes (verified against the game's own UnityEngine dlls, 2022.3):
//  - This URP build never reads CameraEvent command buffers, so
//    camera.AddCommandBuffer(CameraEvent.AfterEverything, ...) is silently
//    ignored. Grabs instead execute a CommandBuffer from the
//    RenderPipelineManager.endCameraRendering handler and read back the blit
//    target with AsyncGPUReadback (the queued blit only runs when the SRP
//    submits the frame, so a synchronous ReadPixels would see stale data).
//  - XRSettings.mirrorViewBlitMode does not exist; the mirror is driven via
//    XRDisplaySubsystem.SetPreferredMirrorBlitMode (SideBySide == -3).
//  - Single-pass instanced (OpenXR default): the XR target is a 2x-wide
//    texture holding L|R side by side, so one blit yields both eyes (layout
//    "side-by-side"). Multi-pass: the blit runs once per eye and we keep the
//    last one; use the mirror.png for the definitive both-eyes view.
public static class VrDebugDump
{
    private const string TriggerFileName = "dump.trigger";

    private static bool _initialized;
    private static bool _armed;
    private static int _dumpFrame = -1;
    private static string? _dir;
    private static string? _triggerSource;

    private static int _pendingReadbacks;
    private static int _pendingSinceFrame;
    private static readonly List<EyeGrab> Grabs = new();
    private static readonly HashSet<int> GrabbedTextureIds = new();

    private static readonly List<CameraEntry> CameraEntries = new();
    private static readonly List<VolumeEntry> VolumeEntries = new();
    private static string? _blendedColorAdjustments;
    private static readonly List<CanvasEntry> CanvasEntries = new();
    private static readonly List<HudGraphicEntry> HudGraphicEntries = new();
    private static readonly List<ScreenGraphicEntry> ScreenGraphicEntries = new();
    private static readonly List<ImageEntry> ImageEntries = new();
    private static readonly List<string> Notes = new();
    private static readonly Dictionary<string, string?> Globals = new();
    private static readonly Dictionary<string, string> ConfigValues = new();

    // URP's shared screen-space textures — mostly null (URP binds them
    // per-draw), but documenting the binding state is exactly the point.
    private static readonly string[] GlobalTextures =
    {
        "_CameraColorTexture", "_CameraDepthTexture", "_CameraOpaqueTexture",
        "_CameraNormalsTexture", "_CameraDepthNormalsTexture", "_BlitTexture",
        "_AfterPostProcessTexture", "_Bloom_Texture",
    };

    public static void Initialize()
    {
        if (_initialized) return;
        _initialized = true;
        RenderPipelineManager.endCameraRendering += OnCameraEndRender;
    }

    public static void Request(string trigger = "F1")
    {
        if (_armed) return;
        _armed = true;
        _dumpFrame = Time.frameCount;
        _triggerSource = trigger;
        _dir = Path.Combine(NOVRPlugin.ModFolderPath, "dumps", DateTime.Now.ToString("HHmmss"));
        Directory.CreateDirectory(_dir);
        Notes.Clear();
        ImageEntries.Clear();
        Grabs.Clear();
        GrabbedTextureIds.Clear();
        CameraEntries.Clear();
        VolumeEntries.Clear();
        _blendedColorAdjustments = null;
        CanvasEntries.Clear();
        HudGraphicEntries.Clear();
        ScreenGraphicEntries.Clear();
        Globals.Clear();
        ConfigValues.Clear();
        _pendingReadbacks = 0;

        SweepCameras();
        SweepVolumes();
        SweepHudGraphics();
        SweepCanvases();
        SweepGlobals();
        SweepConfig();
        ArmEyeGrabs();
        ForceSideBySideMirror();

        Debug.Log($"[NOVR-DUMP] Dump armed for frame {_dumpFrame} -> {_dir}");
    }

    // Called every Update by DebugDumpController. Finalizes once the armed
    // frame's rendering is behind us AND the async eye readbacks have landed
    // (or a generous timeout elapses).
    public static void Tick()
    {
        if (!_armed) return;
        if (Time.frameCount <= _dumpFrame) return;

        if (_pendingReadbacks > 0)
        {
            if (_pendingSinceFrame == 0) _pendingSinceFrame = Time.frameCount;
            if (Time.frameCount - _pendingSinceFrame < 60) return;
            Notes.Add($"timed out waiting for {_pendingReadbacks} async readback(s); eye images incomplete");
            _pendingReadbacks = 0;
        }

        Finalize();
    }

    private static void Finalize()
    {
        _armed = false;
        _pendingSinceFrame = 0;

        try
        {
            // Captures at the end of THIS frame — the mirror is still SideBySide
            // because the restore below only affects the next frame's mirror.
            ScreenCapture.CaptureScreenshot(Path.Combine(_dir!, "mirror.png"));
            ImageEntries.Add(new ImageEntry
            {
                File = "mirror.png",
                Label = "mirror",
                Kind = "mirror",
                Width = Screen.width,
                Height = Screen.height,
                Format = "backbuffer",
                Layout = "side-by-side",
            });
        }
        catch (Exception exception)
        {
            Notes.Add($"mirror screenshot failed: {exception.Message}");
        }

        RestoreMirrorMode();

        SweepRemainingTargetTextures();

        WriteMeta();
        Debug.Log($"[NOVR-DUMP] Dump written to {_dir}");
        _dir = null;
    }

    // ------------------------------------------------------------------ //
    //  Arming
    // ------------------------------------------------------------------ //

    private static void ArmEyeGrabs()
    {
        var targets = new List<(Camera Camera, string Label)>();

        var main = Camera.main ?? APIBus.MainCamera;
        if (main != null) targets.Add((main, "main"));

        if (NOUIManager.I != null && NOUIManager.I.CockpitHudCamera != null)
        {
            targets.Add((NOUIManager.I.CockpitHudCamera, "hudOverlay"));
        }

        foreach (var (camera, label) in targets)
        {
            try
            {
                ArmEyeGrab(camera, label);
            }
            catch (Exception exception)
            {
                Notes.Add($"arm eye grab '{label}' failed: {exception.Message}");
            }
        }
    }

    private static void ArmEyeGrab(Camera camera, string label)
    {
        var eyeWidth = XRSettings.eyeTextureWidth > 0 ? XRSettings.eyeTextureWidth : camera.pixelWidth;
        var eyeHeight = XRSettings.eyeTextureHeight > 0 ? XRSettings.eyeTextureHeight : camera.pixelHeight;
        if (eyeWidth < 2 || eyeHeight < 2)
        {
            Notes.Add($"arm eye grab '{label}': no eye size (xrEnabled={XRSettings.enabled})");
            return;
        }

        var singlePass = XRSettings.stereoRenderingMode == XRSettings.StereoRenderingMode.SinglePassInstanced ||
                         XRSettings.stereoRenderingMode == XRSettings.StereoRenderingMode.SinglePass ||
                         XRSettings.stereoRenderingMode == XRSettings.StereoRenderingMode.SinglePassMultiview;
        var width = singlePass ? eyeWidth * 2 : eyeWidth;

        var rt = RenderTexture.GetTemporary(width, eyeHeight, 0, RenderTextureFormat.ARGB32);
        Grabs.Add(new EyeGrab
        {
            Camera = camera,
            Label = label,
            Rt = rt,
            LayoutSideBySide = singlePass,
            Eye = singlePass ? "LR" : "R",
        });
    }

    private static void ForceSideBySideMirror()
    {
        try
        {
            if (!XRSettings.enabled) return;
            var display = GetDisplaySubsystem();
            if (display == null) return;
            display.SetPreferredMirrorBlitMode(XRMirrorViewBlitMode.SideBySide);
        }
        catch (Exception exception)
        {
            Notes.Add($"mirror mode set failed: {exception.Message}");
        }
    }

    private static void RestoreMirrorMode()
    {
        try
        {
            if (!XRSettings.enabled) return;
            var display = GetDisplaySubsystem();
            if (display == null) return;
            var previous = display.GetPreferredMirrorBlitMode();
            if (previous == XRMirrorViewBlitMode.SideBySide) return;
            display.SetPreferredMirrorBlitMode(previous);
        }
        catch (Exception exception)
        {
            Notes.Add($"mirror mode restore failed: {exception.Message}");
        }
    }

    // ------------------------------------------------------------------ //
    //  Per-camera capture (SRP event, fires for every camera every frame)
    // ------------------------------------------------------------------ //

    private static void OnCameraEndRender(ScriptableRenderContext context, Camera camera)
    {
        if (!_armed || Time.frameCount != _dumpFrame) return;

        try
        {
            var entry = CameraEntries.Find(c => c.Camera == camera);
            if (entry != null) entry.Rendered = true;

            if (camera.targetTexture != null)
            {
                GrabTexture(camera.targetTexture, $"targetTexture:{camera.name}", "texture");
            }

            for (var i = Grabs.Count - 1; i >= 0; i--)
            {
                var grab = Grabs[i];
                if (grab.Camera != camera) continue;

                var rt = grab.Rt;
                var label = grab.Label;
                try
                {
                    var cb = new CommandBuffer { name = "NOVR-Dump-" + label };
                    cb.Blit(BuiltinRenderTextureType.CameraTarget, rt);
                    context.ExecuteCommandBuffer(cb);
                    cb.Release();

                    _pendingReadbacks++;
                    AsyncGPUReadback.Request(rt, 0, TextureFormat.RGBA32, request =>
                    {
                        _pendingReadbacks--;
                        try
                        {
                            if (request.hasError)
                            {
                                Notes.Add($"eye grab '{label}' readback failed");
                                return;
                            }

                            var data = request.GetData<byte>();
                            var texture = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false);
                            texture.LoadRawTextureData(data);
                            texture.Apply();

                            var file = $"eye_{label}.png";
                            File.WriteAllBytes(Path.Combine(_dir!, file), texture.EncodeToPNG());
                            UnityEngine.Object.Destroy(texture);

                            ImageEntries.Add(new ImageEntry
                            {
                                File = file,
                                Label = $"eye {label}",
                                Kind = "eye",
                                Eye = grab.Eye,
                                Width = rt.width,
                                Height = rt.height,
                                Format = rt.format.ToString(),
                                Layout = grab.LayoutSideBySide ? "side-by-side" : "single",
                            });
                        }
                        catch (Exception exception)
                        {
                            Notes.Add($"eye grab '{label}' save failed: {exception.Message}");
                        }
                        finally
                        {
                            RenderTexture.ReleaseTemporary(rt);
                        }
                    });
                }
                catch (Exception exception)
                {
                    Notes.Add($"eye grab '{label}' failed: {exception.Message}");
                    _pendingReadbacks = Math.Max(0, _pendingReadbacks - 1);
                }

                Grabs.RemoveAt(i);
            }
        }
        catch (Exception exception)
        {
            Notes.Add($"endCameraRender handling failed: {exception.Message}");
        }
    }

    // ------------------------------------------------------------------ //
    //  Sweeps (state inventory, taken at arm time / finalize)
    // ------------------------------------------------------------------ //

    private static void SweepCameras()
    {
        // Camera.GetAllCameras returns only enabled, active cameras. The one
        // camera every VR HUD projection is computed in is the game's original
        // "Main Camera", whose Camera component NOVR disables when it parents
        // "NOVR Main Camera" under it — so it never appeared in a dump, and its
        // pose has never been measured. Same trap as the canvas sweep: absent
        // and switched off have to read differently.
        var cameras = Resources.FindObjectsOfTypeAll<Camera>();
        foreach (var camera in cameras)
        {
            if (camera == null || !camera.gameObject.scene.IsValid()) continue;
            var entry = new CameraEntry { Camera = camera };
            try
            {
                entry.Name = FullPath(camera.transform);
                entry.Enabled = camera.enabled;
                entry.ActiveInHierarchy = camera.gameObject.activeInHierarchy;
                entry.Depth = camera.depth;
                entry.PixelWidth = camera.pixelWidth;
                entry.PixelHeight = camera.pixelHeight;
                entry.StereoTargetEye = (int)camera.stereoTargetEye;
                entry.StereoEnabled = camera.stereoEnabled;
                entry.Position = camera.transform.position;
                entry.Euler = camera.transform.eulerAngles;
                entry.Right = camera.transform.right;
                entry.Up = camera.transform.up;
                entry.Forward = camera.transform.forward;
                entry.TargetTexture = camera.targetTexture == null
                    ? null
                    : $"{camera.targetTexture.name} (id={camera.targetTexture.GetInstanceID()}) " +
                      $"{camera.targetTexture.width}x{camera.targetTexture.height} {camera.targetTexture.format}";
                entry.WorldToCamera = TryMatrix(() => camera.worldToCameraMatrix);
                entry.Projection = TryMatrix(() => camera.projectionMatrix);
                entry.StereoViewL = TryMatrix(() => camera.GetStereoViewMatrix(Camera.StereoscopicEye.Left));
                entry.StereoViewR = TryMatrix(() => camera.GetStereoViewMatrix(Camera.StereoscopicEye.Right));
                entry.StereoProjL = TryMatrix(() => camera.GetStereoProjectionMatrix(Camera.StereoscopicEye.Left));
                entry.StereoProjR = TryMatrix(() => camera.GetStereoProjectionMatrix(Camera.StereoscopicEye.Right));
                entry.Urp = DescribeUrpCameraData(camera);
            }
            catch (Exception exception)
            {
                Notes.Add($"camera sweep '{camera.name}' failed: {exception.Message}");
            }
            CameraEntries.Add(entry);
        }
    }

    // Post-processing state per camera. A grade that is configured correctly and
    // still does nothing is usually a camera that never runs post at all, or one
    // whose volume mask excludes the layer the volume lives on — neither of which
    // is visible in a screenshot, so record both.
    //
    // GetComponent, not GetUniversalAdditionalCameraData(): the extension method
    // adds the component when it is missing, which would make the dump report a
    // camera it just changed.
    private static string? DescribeUrpCameraData(Camera camera)
    {
        var data = camera.GetComponent<UniversalAdditionalCameraData>();
        if (data == null) return null;

        var stack = data.renderType == CameraRenderType.Base && data.cameraStack != null
            ? string.Join("+", data.cameraStack.ConvertAll(c => c == null ? "<null>" : c.name).ToArray())
            : "";

        return $"renderType={data.renderType} postFX={data.renderPostProcessing} " +
               $"volumeMask=0x{data.volumeLayerMask.value:x} " +
               $"volumeTrigger={(data.volumeTrigger != null ? data.volumeTrigger.name : "(self)")} " +
               $"antialiasing={data.antialiasing}" +
               (stack.Length > 0 ? $" stack=[{stack}]" : "");
    }

    private static readonly FieldInfo? InternalProfileField =
        typeof(Volume).GetField("m_InternalProfile", BindingFlags.NonPublic | BindingFlags.Instance);

    private static void SweepVolumes()
    {
        try
        {
            foreach (var volume in UnityEngine.Object.FindObjectsOfType<Volume>())
            {
                var layer = volume.gameObject.layer;
                var layerName = LayerMask.LayerToName(layer);
                var entry = new VolumeEntry
                {
                    Name = volume.gameObject.name,
                    Layer = string.IsNullOrEmpty(layerName) ? layer.ToString() : $"{layer} ({layerName})",
                    Enabled = volume.enabled && volume.gameObject.activeInHierarchy,
                    IsGlobal = volume.isGlobal,
                    Priority = volume.priority,
                    Weight = volume.weight,
                };

                // Not volume.profile: that getter instantiates a private copy of a
                // shared profile, which would change what the game renders just by
                // dumping it. This URP version has no profileRef, so read the same
                // pair it would — the instantiated profile if there is one, the
                // shared asset otherwise.
                var profile = InternalProfileField?.GetValue(volume) as VolumeProfile ?? volume.sharedProfile;
                entry.Profile = profile != null ? profile.name : null;
                if (profile != null && profile.TryGet<ColorAdjustments>(out var colorAdjustments))
                {
                    entry.ColorAdjustments =
                        $"active={colorAdjustments.active} " +
                        $"contrast={F(colorAdjustments.contrast.value)}/{colorAdjustments.contrast.overrideState} " +
                        $"saturation={F(colorAdjustments.saturation.value)}/{colorAdjustments.saturation.overrideState} " +
                        $"postExposure={F(colorAdjustments.postExposure.value)}/{colorAdjustments.postExposure.overrideState}";
                }

                VolumeEntries.Add(entry);
            }
        }
        catch (Exception exception)
        {
            Notes.Add($"volume sweep failed: {exception.Message}");
        }

        // The blended stack is what the post pass actually samples. If our values
        // are not in here, no camera ever saw the volume; if they are, the loss is
        // downstream (a camera that skips post entirely).
        try
        {
            var blended = VolumeManager.instance?.stack?.GetComponent<ColorAdjustments>();
            _blendedColorAdjustments = blended == null
                ? "(no ColorAdjustments in the blended stack)"
                : $"active={blended.active} contrast={F(blended.contrast.value)} " +
                  $"saturation={F(blended.saturation.value)} postExposure={F(blended.postExposure.value)}";
        }
        catch (Exception exception)
        {
            _blendedColorAdjustments = $"(read failed: {exception.Message})";
        }
    }

    /// <summary>
    /// Inventory of every graphic on the canvases NOVR moved to world space,
    /// each with its angle off the canvas camera's forward and its viewport
    /// position.
    ///
    /// The canvas list says which canvases exist and where they are; it cannot
    /// answer "what is that white rectangle out to the side", because in VR a
    /// whole canvas shares one transform and everything interesting is in the
    /// element layout underneath it. The viewport coordinate is what makes this
    /// usable in practice: find the thing in mirror.png, read off its position,
    /// look it up here.
    ///
    /// Measure against the canvas's own worldCamera, not Camera.main and not
    /// the cockpit HUD reference. NOVR's world-space UI does not live next to
    /// the aircraft: the canvases sit around the origin (HUDCanvas is at
    /// (0,0,1000)) with the VR overlay camera parked there among them.
    /// Comparing them to anything in aircraft space is comparing two unrelated
    /// coordinate systems, and it produces numbers that look real — the first
    /// version of this sweep put NOVR's own pitch ladder 173 degrees off axis.
    /// </summary>
    private static void SweepHudGraphics()
    {
        try
        {
            var count = 0;
            // FindObjectsOfType skips inactive objects, so a canvas that has
            // been switched off reads identically to one that does not exist —
            // and those need opposite fixes. It cost a run to notice: the sweep
            // reported no HUDCanvas at all while the harness, walking the
            // hierarchy directly, was reporting SceneEssentials/Canvas/HUDCanvas
            // present with an inactive parent.
            foreach (var canvas in Resources.FindObjectsOfTypeAll<Canvas>())
            {
                if (canvas == null || !canvas.isRootCanvas) continue;
                if (!canvas.gameObject.scene.IsValid()) continue;
                if (canvas.renderMode != RenderMode.WorldSpace)
                {
                    SweepScreenCanvas(canvas);
                    continue;
                }

                var viewCamera = canvas.worldCamera;
                if (viewCamera == null) continue;

                var eye = viewCamera.transform.position;
                var forward = viewCamera.transform.forward;
                var up = viewCamera.transform.up;

                foreach (var graphic in canvas.GetComponentsInChildren<Graphic>(true))
                {
                    if (graphic == null) continue;
                    if (count++ >= 4000) break;

                    var direction = graphic.transform.position - eye;
                    if (direction.sqrMagnitude < 1e-6f) continue;

                    // Signed, so a symmetric pair of strays reads as ±N rather
                    // than as two unrelated numbers.
                    var flat = Vector3.ProjectOnPlane(direction, up);
                    var yaw = Vector3.SignedAngle(forward, flat, up);
                    var pitch = Vector3.Angle(flat, direction) * (Vector3.Dot(direction, up) < 0f ? -1f : 1f);

                    var rect = graphic.rectTransform;
                    HudGraphicEntries.Add(new HudGraphicEntry
                    {
                        Path = canvas.name + "/" + PathUnder(canvas.transform, graphic.transform),
                        Type = graphic.GetType().Name,
                        Active = graphic.isActiveAndEnabled,
                        // Distinct from Active: OffscreenGraphicCuller hides
                        // parked UI by culling the renderer, not by
                        // deactivating it, so a dump that reported only Active
                        // would show no difference at all.
                        Culled = graphic.canvasRenderer != null && graphic.canvasRenderer.cull,
                        Color = graphic.color,
                        Yaw = yaw,
                        Pitch = pitch,
                        Size = rect != null ? rect.rect.size : Vector2.zero,
                        LocalPosition = graphic.transform.localPosition,
                        Viewport = viewCamera.WorldToViewportPoint(graphic.transform.position),
                    });
                }
            }

            HudGraphicEntries.Sort((a, b) => Mathf.Abs(b.Yaw).CompareTo(Mathf.Abs(a.Yaw)));
        }
        catch (Exception exception)
        {
            Notes.Add($"hud graphic sweep failed: {exception.Message}");
        }
    }

    /// <summary>
    /// Inventory of a canvas NOVR left in screen space, in that canvas's own
    /// pixels.
    ///
    /// <para>This exists because the world-space sweep could not see the thing
    /// most often under test. <c>HUDCanvas</c> — the flight HUD's own canvas,
    /// carrying the pitch ladder, the velocity vector, the unit markers and the
    /// whole landing symbology — is <c>ScreenSpaceOverlay</c>. It is captured to
    /// a texture and shown on a panel, so it never becomes a world-space canvas
    /// and the sweep skipped it outright: a dump could show a canvas called
    /// HUDCanvas existing and say nothing whatever about what was drawn on
    /// it. Answering "is the runway outline where it should be" needed a
    /// bespoke probe every time.</para>
    ///
    /// <para>Reported as pixels from the canvas's bottom-left rather than as a
    /// world position, because the answer wanted is always "where on the panel",
    /// and a world position has the canvas's own scale folded into it — the
    /// factor that turns a 1920x1080 layout into a 2560x1440 rect and makes two
    /// correct-looking numbers disagree by a third.</para>
    /// </summary>
    private static void SweepScreenCanvas(Canvas canvas)
    {
        var rect = canvas.transform as RectTransform;
        if (rect == null) return;

        var size = rect.rect.size;
        var count = 0;

        foreach (var graphic in canvas.GetComponentsInChildren<Graphic>(true))
        {
            if (graphic == null) continue;
            if (count++ >= 2000) break;

            var local = canvas.transform.InverseTransformPoint(graphic.transform.position);
            var graphicRect = graphic.rectTransform;

            ScreenGraphicEntries.Add(new ScreenGraphicEntry
            {
                Path = canvas.name + "/" + PathUnder(canvas.transform, graphic.transform),
                Type = graphic.GetType().Name,
                Active = graphic.isActiveAndEnabled,
                Culled = graphic.canvasRenderer != null && graphic.canvasRenderer.cull,
                Color = graphic.color,
                Size = graphicRect != null ? graphicRect.rect.size : Vector2.zero,
                Pixels = new Vector2(local.x + size.x * 0.5f, local.y + size.y * 0.5f),
                CanvasSize = size,
                LossyScale = graphic.transform.lossyScale,
            });
        }
    }

    /// <summary>
    /// Full scene path. Canvas names are not unique — this scene has two
    /// GameObjects called "Canvas", one of them the parent of HUDCanvas — and a
    /// bare name in the inventory makes them the same row.
    /// </summary>
    private static string FullPath(Transform node)
    {
        var path = node.name;
        for (var t = node.parent; t != null; t = t.parent) path = t.name + "/" + path;
        return path;
    }

    private static string PathUnder(Transform root, Transform node)
    {
        var path = node.name;
        for (var t = node.parent; t != null && t != root; t = t.parent)
        {
            path = t.name + "/" + path;
        }
        return path;
    }

    private static void SweepCanvases()
    {
        // Same reason as the graphic sweep: an inactive canvas has to be
        // distinguishable from an absent one.
        var canvases = Resources.FindObjectsOfTypeAll<Canvas>();
        var count = 0;
        foreach (var canvas in canvases)
        {
            if (canvas == null || !canvas.gameObject.scene.IsValid()) continue;
            if (count++ >= 100) break;
            try
            {
                var rect = canvas.transform as RectTransform;
                CanvasEntries.Add(new CanvasEntry
                {
                    Name = FullPath(canvas.transform),
                    Enabled = canvas.enabled,
                    Active = canvas.gameObject.activeInHierarchy,
                    SelfActive = canvas.gameObject.activeSelf,
                    RenderMode = (int)canvas.renderMode,
                    WorldCamera = canvas.worldCamera != null ? canvas.worldCamera.name : null,
                    PlaneDistance = canvas.planeDistance,
                    SortingOrder = canvas.sortingOrder,
                    SortingLayer = canvas.sortingLayerName,
                    SizeDelta = rect != null ? new Vector2(rect.sizeDelta.x, rect.sizeDelta.y) : Vector2.zero,
                    Position = canvas.transform.position,
                });
            }
            catch (Exception exception)
            {
                Notes.Add($"canvas sweep '{canvas.name}' failed: {exception.Message}");
            }
        }
    }

    private static void SweepGlobals()
    {
        foreach (var name in GlobalTextures)
        {
            Texture? texture = null;
            try
            {
                texture = Shader.GetGlobalTexture(name);
            }
            catch
            {
                // leave null
            }

            if (texture == null)
            {
                Globals[name] = null;
            }
            else
            {
                Globals[name] = $"id={texture.GetInstanceID()} {texture.width}x{texture.height} {texture.GetType().Name}" +
                                (texture is RenderTexture rt ? $" {rt.format}" : "");
            }
        }
    }

    private static void SweepConfig()
    {
        var instance = ModConfiguration.Instance;
        if (instance == null) return;

        var type = typeof(ModConfiguration);
        foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
        {
            if (field.DeclaringType != type) continue;
            if (field.Name is "Instance" or "Config") continue;

            try
            {
                var value = field.GetValue(instance);
                if (value is ConfigEntryBase entry)
                {
                    ConfigValues[$"{entry.Definition.Section}.{entry.Definition.Key}"] = entry.BoxedValue?.ToString() ?? "null";
                }
            }
            catch (Exception exception)
            {
                Notes.Add($"config sweep '{field.Name}' failed: {exception.Message}");
            }
        }
    }

    private static void SweepRemainingTargetTextures()
    {
        var cameras = new Camera[Camera.allCamerasCount];
        Camera.GetAllCameras(cameras);
        foreach (var camera in cameras)
        {
            if (camera.targetTexture == null) continue;
            if (GrabbedTextureIds.Contains(camera.targetTexture.GetInstanceID())) continue;
            GrabTexture(camera.targetTexture, $"targetTexture:{camera.name}", "texture", stale: true);
        }
    }

    // ------------------------------------------------------------------ //
    //  Texture saving
    // ------------------------------------------------------------------ //

    private static void GrabTexture(Texture texture, string label, string kind, bool stale = false)
    {
        var id = texture.GetInstanceID();
        if (!GrabbedTextureIds.Add(id)) return;

        try
        {
            var width = Mathf.Min(texture.width, 4096);
            var height = Mathf.Min(texture.height, 4096);
            if (width < 2 || height < 2) return;

            var previous = RenderTexture.active;
            var temp = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32);
            try
            {
                Graphics.Blit(texture, temp);
                var file = $"tex_{ImageEntries.Count:D2}.png";
                SaveRenderTexture(temp, Path.Combine(_dir!, file));
                ImageEntries.Add(new ImageEntry
                {
                    File = file,
                    Label = stale ? $"{label} (stale)" : label,
                    Kind = kind,
                    Width = temp.width,
                    Height = temp.height,
                    Format = texture is RenderTexture rt ? rt.format.ToString() : texture.GetType().Name,
                });
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(temp);
            }
        }
        catch (Exception exception)
        {
            Notes.Add($"grab '{label}' failed: {exception.Message}");
        }
    }

    private static void SaveRenderTexture(RenderTexture rt, string path)
    {
        var previous = RenderTexture.active;
        RenderTexture.active = rt;
        try
        {
            var readable = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false);
            readable.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            readable.Apply();
            File.WriteAllBytes(path, readable.EncodeToPNG());
            UnityEngine.Object.Destroy(readable);
        }
        finally
        {
            RenderTexture.active = previous;
        }
    }

    // ------------------------------------------------------------------ //
    //  Meta output (meta.txt for humans, meta.json for the HTML viewer)
    // ------------------------------------------------------------------ //

    private static void WriteMeta()
    {
        File.WriteAllText(Path.Combine(_dir!, "meta.json"), BuildJson());

        var txt = new StringBuilder();
        txt.AppendLine($"=== NOVR dump frame {_dumpFrame} (trigger {_triggerSource}) ===");
        txt.AppendLine($"time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}  scene: {SceneManager.GetActiveScene().name}  novr: {NovrVersion()}");
        txt.AppendLine($"XR: enabled={XRSettings.enabled} stereoRenderingMode={(int)XRSettings.stereoRenderingMode} " +
                       $"eyeTexture={XRSettings.eyeTextureWidth}x{XRSettings.eyeTextureHeight}");

        txt.AppendLine();
        txt.AppendLine("--- cameras ---");
        foreach (var c in CameraEntries)
        {
            txt.AppendLine($"{c.Name}: enabled={c.Enabled} active={c.ActiveInHierarchy} depth={c.Depth} {c.PixelWidth}x{c.PixelHeight} " +
                           $"stereo={(int)c.StereoTargetEye} targetTexture={(c.TargetTexture ?? "<null>")} rendered={c.Rendered}");
            txt.AppendLine($"  pos={Vec(c.Position)} euler={Vec(c.Euler)}");
            txt.AppendLine($"  right={Vec(c.Right)} up={Vec(c.Up)} forward={Vec(c.Forward)}");
            if (c.Urp != null) txt.AppendLine($"  urp: {c.Urp}");
            if (c.WorldToCamera != null) txt.AppendLine($"  worldToCamera:\n{c.WorldToCamera}");
            if (c.Projection != null) txt.AppendLine($"  projection:\n{c.Projection}");
            if (c.StereoViewL != null) txt.AppendLine($"  stereoView[L]:\n{c.StereoViewL}");
            if (c.StereoViewR != null) txt.AppendLine($"  stereoView[R]:\n{c.StereoViewR}");
            if (c.StereoProjL != null) txt.AppendLine($"  stereoProj[L]:\n{c.StereoProjL}");
            if (c.StereoProjR != null) txt.AppendLine($"  stereoProj[R]:\n{c.StereoProjR}");
        }

        txt.AppendLine();
        txt.AppendLine("--- volumes ---");
        foreach (var v in VolumeEntries)
        {
            txt.AppendLine($"{v.Name}: layer={v.Layer} enabled={v.Enabled} global={v.IsGlobal} " +
                           $"priority={F(v.Priority)} weight={F(v.Weight)} profile={(v.Profile ?? "<null>")}");
            if (v.ColorAdjustments != null) txt.AppendLine($"  ColorAdjustments: {v.ColorAdjustments}");
        }
        txt.AppendLine($"blended stack ColorAdjustments: {_blendedColorAdjustments ?? "<not read>"}");

        txt.AppendLine();
        txt.AppendLine("--- hud graphics (most off-axis first) ---");
        foreach (var g in HudGraphicEntries)
        {
            txt.AppendLine($"{F(g.Yaw),8}deg yaw {F(g.Pitch),8}deg pitch  {g.Type,-16} active={g.Active} culled={g.Culled} " +
                           $"rgba=({F(g.Color.r)},{F(g.Color.g)},{F(g.Color.b)},{F(g.Color.a)}) " +
                           $"size={Vec(g.Size)} local={Vec(g.LocalPosition)} " +
                           $"viewport=({F(g.Viewport.x)},{F(g.Viewport.y)},{F(g.Viewport.z)})  {g.Path}");
        }

        txt.AppendLine();
        // Sorted by path, not by whether it is drawn. Sorting drawn-first
        // buried the thing this section is usually opened to find — a symbol
        // that should be on screen and is not — under four hundred entries that
        // were fine, and then the cap cut it. Grouping by canvas keeps a
        // subtree readable as a subtree.
        txt.AppendLine("--- screen-space hud graphics (canvas pixels from bottom-left) ---");
        ScreenGraphicEntries.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
        var screenShown = 0;
        foreach (var g in ScreenGraphicEntries)
        {
            if (screenShown++ >= 4000) break;
            txt.AppendLine($"({F(g.Pixels.x)},{F(g.Pixels.y)}) of {Vec(g.CanvasSize)}  {g.Type,-16} " +
                           $"active={g.Active} culled={g.Culled} " +
                           $"rgba=({F(g.Color.r)},{F(g.Color.g)},{F(g.Color.b)},{F(g.Color.a)}) " +
                           $"size={Vec(g.Size)} scale={Vec(g.LossyScale)}  {g.Path}");
        }
        if (ScreenGraphicEntries.Count > screenShown)
        {
            txt.AppendLine($"... {ScreenGraphicEntries.Count - screenShown} further screen-space graphics not listed.");
        }

        txt.AppendLine();
        txt.AppendLine("--- canvases ---");
        foreach (var c in CanvasEntries)
        {
            txt.AppendLine($"{c.Name}: enabled={c.Enabled} active={c.Active} selfActive={c.SelfActive} renderMode={c.RenderMode} " +
                           $"worldCamera={(c.WorldCamera ?? "<null>")} planeDistance={F(c.PlaneDistance)} " +
                           $"sortingOrder={c.SortingOrder} layer={c.SortingLayer} size={Vec(c.SizeDelta)} pos={Vec(c.Position)}");
        }

        txt.AppendLine();
        txt.AppendLine("--- global textures ---");
        foreach (var kvp in Globals)
        {
            txt.AppendLine($"  {kvp.Key}: {kvp.Value ?? "<null>"}");
        }

        txt.AppendLine();
        txt.AppendLine("--- config ---");
        foreach (var kvp in ConfigValues)
        {
            txt.AppendLine($"  {kvp.Key}: {kvp.Value}");
        }

        txt.AppendLine();
        txt.AppendLine("--- images ---");
        foreach (var i in ImageEntries)
        {
            txt.AppendLine($"  {i.File}: {i.Label} {i.Width}x{i.Height} {i.Format}" +
                           (i.Layout != null ? $" [{i.Layout}]" : "") + (i.Eye != null ? $" eye={i.Eye}" : ""));
        }

        txt.AppendLine();
        txt.AppendLine("--- notes ---");
        if (Notes.Count == 0)
        {
            txt.AppendLine("  (none)");
        }
        else
        {
            foreach (var note in Notes) txt.AppendLine($"  {note}");
        }

        File.WriteAllText(Path.Combine(_dir!, "meta.txt"), txt.ToString());
    }

    private static string BuildJson()
    {
        var j = new StringBuilder();
        j.AppendLine("{");
        j.AppendLine($"  \"novrVersion\": {J.Str(NovrVersion())},");
        j.AppendLine($"  \"scene\": {J.Str(SceneManager.GetActiveScene().name)},");
        j.AppendLine($"  \"trigger\": {J.Str(_triggerSource ?? "?")},");
        j.AppendLine($"  \"frame\": {_dumpFrame},");
        j.AppendLine($"  \"time\": {J.Str(DateTime.Now.ToString("HHmmss"))},");
        j.AppendLine($"  \"timestamp\": {J.Str(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"))},");
        j.AppendLine($"  \"xr\": {{ \"enabled\": {J.Bool(XRSettings.enabled)}, \"stereoRenderingMode\": {(int)XRSettings.stereoRenderingMode}, \"eyeTexture\": [{XRSettings.eyeTextureWidth}, {XRSettings.eyeTextureHeight}] }},");
        j.AppendLine("  \"cameras\": [");
        for (var i = 0; i < CameraEntries.Count; i++)
        {
            var c = CameraEntries[i];
            j.Append("    {");
            j.Append($" \"name\": {J.Str(c.Name)}, \"enabled\": {J.Bool(c.Enabled)}, \"depth\": {F(c.Depth)}, \"pixel\": [{c.PixelWidth}, {c.PixelHeight}], ");
            j.Append($"\"stereoTargetEye\": {(int)c.StereoTargetEye}, \"stereoEnabled\": {J.Bool(c.StereoEnabled)}, \"rendered\": {J.Bool(c.Rendered)}, ");
            j.Append($"\"targetTexture\": {(c.TargetTexture == null ? "null" : J.Str(c.TargetTexture))}, ");
            j.Append($"\"position\": {J.Vec(c.Position)}, \"euler\": {J.Vec(c.Euler)}, ");
            j.Append($"\"worldToCamera\": {J.Mat(c.WorldToCamera)}, \"projection\": {J.Mat(c.Projection)}, ");
            j.Append($"\"stereoViewL\": {J.Mat(c.StereoViewL)}, \"stereoViewR\": {J.Mat(c.StereoViewR)}, ");
            j.Append($"\"stereoProjL\": {J.Mat(c.StereoProjL)}, \"stereoProjR\": {J.Mat(c.StereoProjR)}, ");
            j.Append($"\"urp\": {(c.Urp == null ? "null" : J.Str(c.Urp))}");
            j.AppendLine(i == CameraEntries.Count - 1 ? " }" : " },");
        }
        j.AppendLine("  ],");
        j.AppendLine("  \"volumes\": [");
        for (var i = 0; i < VolumeEntries.Count; i++)
        {
            var v = VolumeEntries[i];
            j.Append("    {");
            j.Append($" \"name\": {J.Str(v.Name)}, \"layer\": {J.Str(v.Layer)}, \"enabled\": {J.Bool(v.Enabled)}, ");
            j.Append($"\"isGlobal\": {J.Bool(v.IsGlobal)}, \"priority\": {F(v.Priority)}, \"weight\": {F(v.Weight)}, ");
            j.Append($"\"profile\": {(v.Profile == null ? "null" : J.Str(v.Profile))}, ");
            j.Append($"\"colorAdjustments\": {(v.ColorAdjustments == null ? "null" : J.Str(v.ColorAdjustments))}");
            j.AppendLine(i == VolumeEntries.Count - 1 ? " }" : " },");
        }
        j.AppendLine("  ],");
        j.AppendLine($"  \"blendedColorAdjustments\": {(_blendedColorAdjustments == null ? "null" : J.Str(_blendedColorAdjustments))},");
        j.AppendLine("  \"canvases\": [");
        for (var i = 0; i < CanvasEntries.Count; i++)
        {
            var c = CanvasEntries[i];
            j.Append("    {");
            j.Append($" \"name\": {J.Str(c.Name)}, \"enabled\": {J.Bool(c.Enabled)}, \"active\": {J.Bool(c.Active)}, \"renderMode\": {c.RenderMode}, ");
            j.Append($"\"worldCamera\": {(c.WorldCamera == null ? "null" : J.Str(c.WorldCamera))}, \"planeDistance\": {F(c.PlaneDistance)}, ");
            j.Append($"\"sortingOrder\": {c.SortingOrder}, \"sortingLayer\": {J.Str(c.SortingLayer)}, ");
            j.Append($"\"sizeDelta\": {J.Vec(c.SizeDelta)}, \"position\": {J.Vec(c.Position)}");
            j.AppendLine(i == CanvasEntries.Count - 1 ? " }" : " },");
        }
        j.AppendLine("  ],");
        j.AppendLine("  \"globals\": {");
        var gi = 0;
        foreach (var kvp in Globals)
        {
            j.Append($"    {J.Str(kvp.Key)}: {(kvp.Value == null ? "null" : J.Str(kvp.Value))}");
            j.AppendLine(++gi == Globals.Count ? "" : ",");
        }
        j.AppendLine("  },");
        j.AppendLine("  \"config\": {");
        var ci = 0;
        foreach (var kvp in ConfigValues)
        {
            j.Append($"    {J.Str(kvp.Key)}: {J.Str(kvp.Value)}");
            j.AppendLine(++ci == ConfigValues.Count ? "" : ",");
        }
        j.AppendLine("  },");
        j.AppendLine("  \"images\": [");
        for (var i = 0; i < ImageEntries.Count; i++)
        {
            var im = ImageEntries[i];
            j.Append("    {");
            j.Append($" \"file\": {J.Str(im.File)}, \"label\": {J.Str(im.Label)}, \"kind\": {J.Str(im.Kind)}, ");
            j.Append($"\"width\": {im.Width}, \"height\": {im.Height}, \"format\": {J.Str(im.Format)}, ");
            j.Append($"\"eye\": {(im.Eye == null ? "null" : J.Str(im.Eye))}, \"layout\": {(im.Layout == null ? "null" : J.Str(im.Layout))}");
            j.AppendLine(i == ImageEntries.Count - 1 ? " }" : " },");
        }
        j.AppendLine("  ],");
        j.AppendLine("  \"notes\": [");
        for (var i = 0; i < Notes.Count; i++)
        {
            j.Append($"    {J.Str(Notes[i])}");
            j.AppendLine(i == Notes.Count - 1 ? "" : ",");
        }
        j.AppendLine("  ]");
        j.AppendLine("}");
        return j.ToString();
    }

    private static string NovrVersion()
    {
        try
        {
            return typeof(NOVRPlugin).Assembly.GetCustomAttribute<BepInPlugin>()?.Version?.ToString() ?? "?";
        }
        catch
        {
            return "?";
        }
    }

    private static XRDisplaySubsystem? GetDisplaySubsystem()
    {
        var subsystems = new List<XRDisplaySubsystem>();
        SubsystemManager.GetSubsystems(subsystems);
        return subsystems.Count > 0 ? subsystems[0] : null;
    }

    private static string F(float f) => f.ToString("0.######", CultureInfo.InvariantCulture);

    private static string Vec(Vector3 v) => $"({F(v.x)}, {F(v.y)}, {F(v.z)})";

    private static string Vec(Vector2 v) => $"({F(v.x)}, {F(v.y)})";

    private static Matrix4x4? TryMatrix(Func<Matrix4x4> get)
    {
        try
        {
            return get();
        }
        catch
        {
            return null;
        }
    }

    // ------------------------------------------------------------------ //
    //  Entry types
    // ------------------------------------------------------------------ //

    private sealed class EyeGrab
    {
        public Camera? Camera;
        public string Label = "";
        public RenderTexture? Rt;
        public bool LayoutSideBySide;
        public string Eye = "";
    }

    private sealed class CameraEntry
    {
        public Camera? Camera;
        public string Name = "";
        public bool Enabled;
        public bool ActiveInHierarchy;
        public float Depth;
        public int PixelWidth;
        public int PixelHeight;
        public int StereoTargetEye;
        public bool StereoEnabled;
        public string? TargetTexture;
        public Vector3 Position;
        public Vector3 Euler;
        // Euler angles are ambiguous near gimbal lock and cannot be compared
        // between two cameras by eye; the basis vectors can.
        public Vector3 Right;
        public Vector3 Up;
        public Vector3 Forward;
        public Matrix4x4? WorldToCamera;
        public Matrix4x4? Projection;
        public Matrix4x4? StereoViewL;
        public Matrix4x4? StereoViewR;
        public Matrix4x4? StereoProjL;
        public Matrix4x4? StereoProjR;
        public bool Rendered;
        public string? Urp;
    }

    private sealed class VolumeEntry
    {
        public string Name = "";
        public string Layer = "";
        public bool Enabled;
        public bool IsGlobal;
        public float Priority;
        public float Weight;
        public string? Profile;
        public string? ColorAdjustments;
    }

    private sealed class HudGraphicEntry
    {
        public string Path = "";
        public string Type = "";
        public bool Active;
        public bool Culled;
        public Color Color;
        public float Yaw;
        public float Pitch;
        public Vector2 Size;
        public Vector3 LocalPosition;
        public Vector3 Viewport;
    }

    /// <summary>
    /// A graphic on a canvas NOVR did <i>not</i> move to world space. Kept in
    /// its own list and its own section: the world-space sweep measures angles
    /// off a camera, and putting a pixel coordinate in the same table under the
    /// same column headings is how two coordinate systems get read as one.
    /// </summary>
    private sealed class ScreenGraphicEntry
    {
        public string Path = "";
        public string Type = "";
        public bool Active;
        public bool Culled;
        public Color Color;
        public Vector2 Size;
        public Vector2 Pixels;
        public Vector2 CanvasSize;
        public Vector3 LossyScale;
    }

    private sealed class CanvasEntry
    {
        public string Name = "";
        public bool Enabled;
        public bool Active;
        public bool SelfActive;
        public int RenderMode;
        public string? WorldCamera;
        public float PlaneDistance;
        public int SortingOrder;
        public string SortingLayer = "";
        public Vector2 SizeDelta;
        public Vector3 Position;
    }

    private sealed class ImageEntry
    {
        public string File = "";
        public string Label = "";
        public string Kind = "";
        public string? Eye;
        public int Width;
        public int Height;
        public string Format = "";
        public string? Layout;
    }

    private static class J
    {
        public static string Str(string s) =>
            "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "") + "\"";

        public static string Bool(bool b) => b ? "true" : "false";

        public static string Vec(Vector3 v) => $"[{F(v.x)}, {F(v.y)}, {F(v.z)}]";

        public static string Vec(Vector2 v) => $"[{F(v.x)}, {F(v.y)}]";

        public static string Mat(Matrix4x4? m)
        {
            if (m == null) return "null";
            return $"[[{F(m.Value.m00)},{F(m.Value.m01)},{F(m.Value.m02)},{F(m.Value.m03)}]," +
                   $"[{F(m.Value.m10)},{F(m.Value.m11)},{F(m.Value.m12)},{F(m.Value.m13)}]," +
                   $"[{F(m.Value.m20)},{F(m.Value.m21)},{F(m.Value.m22)},{F(m.Value.m23)}]," +
                   $"[{F(m.Value.m30)},{F(m.Value.m31)},{F(m.Value.m32)},{F(m.Value.m33)}]]";
        }
    }
}
