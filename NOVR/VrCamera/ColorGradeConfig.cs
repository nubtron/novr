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

    public static ConfigEntry<float> Gamma;
    public static ConfigEntry<KeyCode> GammaDecreaseShortcut;
    public static ConfigEntry<KeyCode> GammaIncreaseShortcut;

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

        Gamma = config.Bind(
            "Display",
            "Color Gamma",
            -0.3f,
            new ConfigDescription(
                "Gamma adjustment applied to the final image. Negative darkens the midtones, positive lifts them; highlights and blacks move far less than with contrast, which is what makes it the right control for a headset streamer's gamma curve. 0 disables. Adjustable in flight with the two shortcuts below, which write the value back here, so you can tune it in the headset and keep what you picked. The default was tuned in a headset against Virtual Desktop's washed-out image; a different streamer, or none, may want less.",
                new AcceptableValueRange<float>(-1f, 1f)));
        // Live tuning is the point of these: the value that cancels the
        // streamer's curve cannot be judged from a desktop mirror, and quitting
        // to edit a config file loses the comparison you were making.
        GammaDecreaseShortcut = config.Bind(
            "Input",
            "Color Gamma Decrease Shortcut",
            KeyCode.LeftBracket,
            "Keyboard shortcut to darken the midtones by one step (0.05). Rebind if your keyboard layout has no bracket keys.");
        GammaIncreaseShortcut = config.Bind(
            "Input",
            "Color Gamma Increase Shortcut",
            KeyCode.RightBracket,
            "Keyboard shortcut to lift the midtones by one step (0.05). Rebind if your keyboard layout has no bracket keys.");
    }
}
