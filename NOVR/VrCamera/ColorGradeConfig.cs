using BepInEx.Configuration;
using UnityEngine;

namespace NOVR;

/// <summary>
/// The colour grade's settings, owned by the feature that uses them.
///
/// They live here rather than in ModConfiguration because this mod is
/// developed as a stack of independent branches: a setting declared in the
/// shared file puts every branch that adds one in conflict with every other,
/// over lists none of them disagree about. ConfigSections finds this class by
/// its attribute, so the feature is self-contained.
/// </summary>
[ConfigSection(Order = 40)]
public static class ColorGradeConfig
{
    public static ConfigEntry<float> Contrast;
    public static ConfigEntry<float> Saturation;

    public static void Bind(ConfigFile config)
    {
        Contrast = config.Bind(
            "Display",
            "Color Contrast",
            15f,
            new ConfigDescription(
                "Contrast boost applied to the final image to counteract washed-out colors (e.g. a headset streamer's gamma/color mapping). 0 disables.",
                new AcceptableValueRange<float>(-100f, 100f)));
        Saturation = config.Bind(
            "Display",
            "Color Saturation",
            10f,
            new ConfigDescription(
                "Saturation boost applied to the final image. 0 disables.",
                new AcceptableValueRange<float>(-100f, 100f)));
    }
}
