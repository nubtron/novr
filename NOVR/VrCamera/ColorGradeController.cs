using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace NOVR.VrCamera;

/// <summary>
/// Applies a subtle contrast/saturation boost to the final rendered image to
/// counteract washed-out colors (e.g. the headset streamer's gamma/color
/// mapping). Implemented as a global URP Volume with its own
/// ColorAdjustments override so the game's own exposure writes (which share
/// the same component type) are left untouched.
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

    private Volume _volume;
    private ColorAdjustments _colorAdjustments;
    private float _appliedContrast = float.NaN;
    private float _appliedSaturation = float.NaN;
    private int _volumeLayer = -1;
    private float _nextLayerCheck;
    private Camera[] _cameraBuffer = new Camera[8];

    private void Start()
    {
        var profile = ScriptableObject.CreateInstance<VolumeProfile>();
        _colorAdjustments = profile.Add<ColorAdjustments>(false);

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

        ResolveVolumeLayer();
        Apply();
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
        var contrast = ModConfiguration.Instance.ColorContrast.Value;
        var saturation = ModConfiguration.Instance.ColorSaturation.Value;
        if (Mathf.Approximately(contrast, _appliedContrast) &&
            Mathf.Approximately(saturation, _appliedSaturation))
        {
            return;
        }

        _colorAdjustments.contrast.value = contrast;
        _colorAdjustments.contrast.overrideState = true;

        _colorAdjustments.saturation.value = saturation;
        _colorAdjustments.saturation.overrideState = true;

        _appliedContrast = contrast;
        _appliedSaturation = saturation;
    }
}
