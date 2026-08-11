using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace NOVR.VrCamera;

/// <summary>
/// Applies a subtle contrast/saturation/gamma correction to the final rendered
/// image to counteract washed-out colors (e.g. the headset streamer's
/// gamma/color mapping). Implemented as a global URP Volume with its own
/// ColorAdjustments and LiftGammaGain overrides so the game's own exposure
/// writes (which share the same component type) are left untouched.
///
/// Gamma is adjustable in flight with a pair of keys, because the value that
/// cancels a given streamer's curve can only be judged from inside the headset.
/// The keys write back into the config entry, so the tuned value survives the
/// session and can be read out of the config file afterwards.
///
/// The volume lives on its own child object, and that object's *layer* is what
/// decides whether any of this reaches the screen: URP blends only the volumes
/// a camera's volumeLayerMask selects, and only a camera with
/// renderPostProcessing runs the pass that consumes the result. In this game
/// the two are different cameras — the main camera samples the Default layer
/// but has post-processing off, while the "postProcessingRenderer" overlay that
/// does run post samples the PP layer alone. A volume left on Default is
/// therefore blended by a camera that never renders it and ignored by the one
/// that does, which is measurably a no-op. See ResolveVolumeLayer.
/// </summary>
public class ColorGradeController : MonoBehaviour
{
    private const string VolumeObjectName = "NOVR Color Grade";

    // The camera rig is rebuilt on scene loads, so the layer is re-resolved
    // rather than latched at Start. Twice a second is far below anything that
    // matters and keeps the per-frame path free of a camera sweep.
    private const float LayerCheckInterval = 0.5f;

    // One keypress. Small enough that the pilot can stop on the value they
    // want, large enough that the difference between two steps is visible.
    private const float GammaStep = 0.05f;
    private const float GammaMin = -1f;
    private const float GammaMax = 1f;

    // Report the value once the pilot stops pressing, not once per press: the
    // game's message feed keeps a line for eight seconds, so a burst of taps
    // would otherwise fill the cockpit with its own history.
    private const float ReportDelay = 0.4f;

    private Volume _volume;
    private ColorAdjustments _colorAdjustments;
    private LiftGammaGain _liftGammaGain;
    private float _appliedContrast = float.NaN;
    private float _appliedSaturation = float.NaN;
    private float _appliedGamma = float.NaN;
    private int _volumeLayer = -1;
    private float _nextLayerCheck;
    private float _reportAt;
    private Camera[] _cameraBuffer = new Camera[8];

    private void Start()
    {
        var profile = ScriptableObject.CreateInstance<VolumeProfile>();
        _colorAdjustments = profile.Add<ColorAdjustments>(false);
        _liftGammaGain = profile.Add<LiftGammaGain>(false);

        // A child object, not the NOVR root: the layer has to change to reach
        // the post-processing camera, and the root carries the headset,
        // UI and controller behaviours that have no business moving with it.
        var host = new GameObject(VolumeObjectName);
        host.transform.SetParent(transform, false);

        _volume = host.AddComponent<Volume>();
        _volume.isGlobal = true;
        _volume.priority = 1000f; // above the game's volumes so our overrides win per-property
        _volume.profile = profile;

        ResolveVolumeLayer();
        Apply();
    }

    private void Update()
    {
        if (_volume == null || _colorAdjustments == null)
        {
            return;
        }

        HandleGammaShortcuts();
        ResolveVolumeLayer();
        Apply();
        ReportIfDue();
    }

    private void HandleGammaShortcuts()
    {
        var config = ModConfiguration.Instance;
        if (config == null)
        {
            return;
        }

        var steps = 0;
        if (Input.GetKeyDown(ColorGradeConfig.GammaIncreaseShortcut.Value)) steps++;
        if (Input.GetKeyDown(ColorGradeConfig.GammaDecreaseShortcut.Value)) steps--;
        if (steps == 0)
        {
            return;
        }

        // Snapped to the step grid rather than accumulated, so a long tuning
        // session cannot leave the pilot on 0.15000001 and cannot drift off the
        // values they can reproduce by counting keypresses from zero.
        var stepped = Mathf.Round(ColorGradeConfig.Gamma.Value / GammaStep) + steps;
        var value = Mathf.Clamp(stepped * GammaStep, GammaMin, GammaMax);
        if (Mathf.Approximately(value, ColorGradeConfig.Gamma.Value))
        {
            // Already at the end of the range. Still worth reporting: silence
            // here reads as a dropped keypress.
            _reportAt = Time.unscaledTime + ReportDelay;
            return;
        }

        // Writing the config entry is what persists the value; BepInEx saves the
        // file on set. This is also why the shortcuts are usable as the only
        // interface to the setting — what you tuned is what you get next launch.
        ColorGradeConfig.Gamma.Value = value;
        _reportAt = Time.unscaledTime + ReportDelay;
    }

    private void ReportIfDue()
    {
        if (_reportAt <= 0f || Time.unscaledTime < _reportAt)
        {
            return;
        }
        _reportAt = 0f;

        var value = ColorGradeConfig.Gamma.Value;
        var text = $"VR gamma {value:0.00}";
        Debug.Log($"[NOVR] Color gamma set to {value:0.00} (Display / Color Gamma).");

        // The game's own message feed: it is already in the pilot's view, it
        // already survives the world-space canvas conversion, and it costs no
        // UI of our own. It only exists in the flight scenes — outside them the
        // log line above is the whole report.
        try
        {
            var gameplayUi = GameplayUI.i;
            if (gameplayUi != null)
            {
                gameplayUi.GameMessage(text);
            }
        }
        catch (System.Exception e)
        {
            Debug.LogWarning($"[NOVR] Could not show the gamma value in the message feed: {e.Message}");
        }
    }

    /// <summary>
    /// Move the volume onto a layer the post-processing camera actually samples.
    /// The layer is read from that camera's mask rather than hardcoded, because
    /// the name and index of the game's PP layer are its business, not ours.
    /// </summary>
    private void ResolveVolumeLayer()
    {
        if (Time.unscaledTime < _nextLayerCheck)
        {
            return;
        }
        _nextLayerCheck = Time.unscaledTime + LayerCheckInterval;

        var layer = FindPostProcessingVolumeLayer();
        if (layer < 0 || layer == _volumeLayer)
        {
            return;
        }

        _volumeLayer = layer;
        _volume.gameObject.layer = layer;
        Debug.Log($"[NOVR] Color grade volume moved to layer {layer} ({LayerMask.LayerToName(layer)}).");
    }

    private int FindPostProcessingVolumeLayer()
    {
        if (_cameraBuffer.Length < Camera.allCamerasCount)
        {
            _cameraBuffer = new Camera[Camera.allCamerasCount];
        }

        var count = Camera.GetAllCameras(_cameraBuffer);
        for (var i = 0; i < count; i++)
        {
            var camera = _cameraBuffer[i];
            if (camera == null)
            {
                continue;
            }

            // Cameras rendering into a texture are the cockpit's own screens
            // (the tactical display, the target window). They run post-
            // processing too, and grading them would be both pointless and a
            // way to land on the wrong layer.
            if (camera.targetTexture != null)
            {
                continue;
            }

            var data = camera.GetComponent<UniversalAdditionalCameraData>();
            if (data == null || !data.renderPostProcessing)
            {
                continue;
            }

            var mask = data.volumeLayerMask.value;
            if (mask == 0)
            {
                continue;
            }

            for (var layer = 0; layer < 32; layer++)
            {
                if ((mask & (1 << layer)) != 0)
                {
                    return layer;
                }
            }
        }

        return -1;
    }

    private void Apply()
    {
        var contrast = ColorGradeConfig.Contrast.Value;
        var saturation = ColorGradeConfig.Saturation.Value;
        var gamma = ColorGradeConfig.Gamma.Value;
        if (Mathf.Approximately(contrast, _appliedContrast) &&
            Mathf.Approximately(saturation, _appliedSaturation) &&
            Mathf.Approximately(gamma, _appliedGamma))
        {
            return;
        }

        _colorAdjustments.contrast.value = contrast;
        _colorAdjustments.contrast.overrideState = true;

        _colorAdjustments.saturation.value = saturation;
        _colorAdjustments.saturation.overrideState = true;

        // xyz are the per-channel wheel, w the master the inspector's slider
        // drives; only the master is exposed. The override is dropped entirely
        // at 0 rather than written as a neutral value, so a disabled gamma
        // cannot outrank whatever the game's own volumes do with this effect
        // (our priority is 1000, which wins every contest it enters).
        _liftGammaGain.gamma.value = new Vector4(1f, 1f, 1f, gamma);
        _liftGammaGain.gamma.overrideState = !Mathf.Approximately(gamma, 0f);

        _appliedContrast = contrast;
        _appliedSaturation = saturation;
        _appliedGamma = gamma;
    }
}
