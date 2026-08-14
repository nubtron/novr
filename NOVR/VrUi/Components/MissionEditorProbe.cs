using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Cysharp.Threading.Tasks;
using NOVR.VrUi.Capture;
using NuclearOption.MissionEditorScripts;
using NuclearOption.SavedMission;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace NOVR.VrUi.Components;

/// <summary>
/// TEMPORARY probe: opens the game's mission editor headlessly and reports what
/// the UI actually is, so the VR treatment is designed against a measurement
/// rather than against the decompile.
///
/// Driven by trigger files next to NOVR.dll so a live `--keep-running` harness
/// game can be poked from WSL without a rebuild:
///   editor.trigger  — click the main menu's MISSION EDITOR button
///   probe.trigger   — write one inventory report to the log
/// After the editor is opened it reports on a timer as well, because the
/// interesting transition is the one between menu and editor.
/// </summary>
public class MissionEditorProbe : NOVRBehaviour
{
    private const float PollInterval = 1f;
    private const float ReportInterval = 4f;
    private const int MaxAutoReports = 8;

    private float _nextPoll;
    private float _nextReport;
    private int _autoReportsLeft;

    private void Update()
    {
        if (Time.unscaledTime >= _nextPoll)
        {
            _nextPoll = Time.unscaledTime + PollInterval;
            if (ConsumeTrigger("editor.trigger")) OpenMissionEditor();
            if (ConsumeTrigger("probe.trigger")) Report("manual");
        }

        if (_autoReportsLeft > 0 && Time.unscaledTime >= _nextReport)
        {
            _nextReport = Time.unscaledTime + ReportInterval;
            _autoReportsLeft--;
            Report($"auto{MaxAutoReports - _autoReportsLeft}");
        }
    }

    private static bool ConsumeTrigger(string name)
    {
        try
        {
            var path = Path.Combine(NOVRPlugin.ModFolderPath, name);
            if (!File.Exists(path)) return false;
            File.Delete(path);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Open the editor the way the game's own New/Load menu does — the real
    /// API, not synthesised menu clicks, for the same reason
    /// <see cref="AutoStartMission"/> does it that way: it does not break when
    /// the menu layout changes. An existing mission is loaded rather than a
    /// blank one, so the editor comes up with units to select.
    /// </summary>
    private void OpenMissionEditor()
    {
        Report("before-open");

        try
        {
            MissionGroup.Init();
            var missions = MissionSaveLoad.QuickLoadMany(MissionGroup.All.GetMissions()).ToList();
            if (missions.Count == 0)
            {
                Debug.LogWarning("[VPROBE-ED] no missions available to open in the editor.");
                return;
            }

            var chosen = missions[0];
            if (!chosen.key.TryLoad(out var mission, out var error))
            {
                Debug.LogWarning($"[VPROBE-ED] failed to load mission '{chosen.key}': {error}");
                return;
            }

            Debug.Log($"[VPROBE-ED] opening editor with mission '{chosen.key}' (map {mission.MapKey}).");
            MissionEditor.LoadEditor(mission).Forget();

            _autoReportsLeft = MaxAutoReports;
            _nextReport = Time.unscaledTime + ReportInterval;
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[VPROBE-ED] opening the editor threw: {e}");
        }
    }

    private void Report(string tag)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"[VPROBE-ED] ===== report {tag} =====");

        var scenes = new List<string>();
        for (var i = 0; i < SceneManager.sceneCount; i++)
        {
            var scene = SceneManager.GetSceneAt(i);
            scenes.Add($"{scene.name}(loaded={scene.isLoaded})");
        }
        sb.AppendLine($"[VPROBE-ED] scenes: {string.Join(", ", scenes)}  " +
                      $"gameState={SafeGameState()}");

        sb.AppendLine($"[VPROBE-ED] menuCapture active={MenuCaptureBackend.IsActive} " +
                      $"enabled={MenuCaptureBackend.Enabled} " +
                      $"suppressOverlay={MenuCaptureBackend.SuppressesScreenOverlayUi}");

        // Every canvas in the scene, active or not: the question is which ones
        // exist while the editor is up and how they are rendered.
        var canvases = Resources.FindObjectsOfTypeAll<Canvas>()
            .Where(c => c != null && c.gameObject.scene.IsValid())
            .OrderByDescending(c => c.isActiveAndEnabled)
            .ToArray();

        sb.AppendLine($"[VPROBE-ED] canvases ({canvases.Length}):");
        foreach (var canvas in canvases.Take(40))
        {
            var raycaster = canvas.GetComponent<GraphicRaycaster>();
            sb.AppendLine($"[VPROBE-ED]   {canvas.name,-28} active={canvas.isActiveAndEnabled,-5} " +
                          $"mode={canvas.renderMode,-20} cam={(canvas.worldCamera != null ? canvas.worldCamera.name : "<null>"),-22} " +
                          $"order={canvas.sortingOrder,-4} layer={LayerMask.LayerToName(canvas.gameObject.layer),-10} " +
                          $"raycaster={(raycaster != null ? raycaster.enabled.ToString() : "none"),-5} " +
                          $"scene={canvas.gameObject.scene.name} root={PathOf(canvas.gameObject)}");
        }

        // Anything from the editor's own namespace that is alive right now.
        var editorComponents = Resources.FindObjectsOfTypeAll<MonoBehaviour>()
            .Where(m => m != null && m.gameObject.scene.IsValid())
            .Where(m => (m.GetType().Namespace ?? string.Empty).Contains("MissionEditor") ||
                        m.GetType().Name.Contains("MissionEditor"))
            .ToArray();

        sb.AppendLine($"[VPROBE-ED] editor components ({editorComponents.Length}):");
        foreach (var component in editorComponents.Take(25))
        {
            sb.AppendLine($"[VPROBE-ED]   {component.GetType().FullName,-60} " +
                          $"active={component.gameObject.activeInHierarchy} path={PathOf(component.gameObject)}");
        }

        var eventSystem = EventSystem.current;
        sb.AppendLine($"[VPROBE-ED] eventSystem={(eventSystem != null ? eventSystem.name : "<null>")} " +
                      $"selected={(eventSystem != null && eventSystem.currentSelectedGameObject != null ? eventSystem.currentSelectedGameObject.name : "<none>")}");

        var cameras = Camera.allCameras.OrderBy(c => c.depth).ToArray();
        sb.AppendLine($"[VPROBE-ED] enabled cameras ({cameras.Length}): " +
                      string.Join(", ", cameras.Select(c => $"{c.name}(d={c.depth:F0},tex={(c.targetTexture != null ? "rt" : "screen")})")));
        sb.AppendLine($"[VPROBE-ED] Camera.main={(Camera.main != null ? PathOf(Camera.main.gameObject) : "<null>")}");

        Debug.Log(sb.ToString());
    }

    private static string SafeGameState()
    {
        try
        {
            return GameManager.gameState.ToString();
        }
        catch (Exception e)
        {
            return $"<{e.GetType().Name}>";
        }
    }

    private static string PathOf(GameObject go)
    {
        var path = go.name;
        var parent = go.transform.parent;
        var guard = 0;
        while (parent != null && guard++ < 12)
        {
            path = parent.name + "/" + path;
            parent = parent.parent;
        }
        return path;
    }
}
