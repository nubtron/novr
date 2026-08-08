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
/// </summary>
public class ColorGradeController : MonoBehaviour
{
    private Volume _volume;
    private ColorAdjustments _colorAdjustments;
    private float _appliedContrast = float.NaN;
    private float _appliedSaturation = float.NaN;

    private void Start()
    {
        var profile = ScriptableObject.CreateInstance<VolumeProfile>();
        _colorAdjustments = profile.Add<ColorAdjustments>(false);

        _volume = gameObject.AddComponent<Volume>();
        _volume.isGlobal = true;
        _volume.priority = 1000f; // above the game's volumes so our overrides win per-property
        _volume.profile = profile;

        Apply();
    }

    private void Update()
    {
        if (_volume == null || _colorAdjustments == null)
        {
            return;
        }

        Apply();
    }

    private void Apply()
    {
        var contrast = ColorGradeConfig.Contrast.Value;
        var saturation = ColorGradeConfig.Saturation.Value;
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
