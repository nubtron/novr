using BepInEx.Configuration;
using UnityEngine;

namespace NOVR.VrMap;

/// <summary>
/// Settings for the 3D world map — the map as a solid model of the real
/// terrain, floating below you, instead of a picture on a panel.
///
/// They live here rather than in ModConfiguration because this mod is developed
/// as a stack of independent branches: a setting declared in the shared file
/// puts every branch that adds one in conflict with every other. ConfigSections
/// finds this class by its attribute.
/// </summary>
[ConfigSection(Order = 96)]
public static class VrMapConfig
{
    public static ConfigEntry<bool> Enabled;
    public static ConfigEntry<bool> Open;
    public static ConfigEntry<KeyCode> ToggleShortcut;
    public static ConfigEntry<float> Scale;
    public static ConfigEntry<float> EyeHeight;
    public static ConfigEntry<float> ReliefExaggeration;
    public static ConfigEntry<bool> HideCockpit;
    public static ConfigEntry<bool> DebugMarker;

    public static void Bind(ConfigFile config)
    {
        Enabled = config.Bind(
            "Experimental",
            "World Map",
            true,
            "Make the 3D world map available. It is the whole map as a solid model of the real "
            + "terrain — the game's own ground, at its own colours — hanging below you at model "
            + "scale, so you look down on the theatre the way you would from a very long way up, "
            + "but close enough that both eyes see it as a solid thing rather than a flat picture. "
            + "That last part is the entire reason it exists: a real map seen from 40 km has no "
            + "depth to give, and a model 12 m away does. It does not replace the game's own map — "
            + "open that as usual. Turning this off costs nothing while the map is closed.");

        Open = config.Bind(
            "Experimental",
            "World Map Open",
            false,
            "Whether the 3D world map is currently showing. The shortcut below writes this, so "
            + "whatever you left it as is what you get next time. It is a setting rather than "
            + "pure runtime state so that a harness run can open the map without pressing a key.");

        ToggleShortcut = config.Bind(
            "Input",
            "World Map Shortcut",
            KeyCode.F10,
            "Keyboard shortcut that shows and hides the 3D world map.");

        Scale = config.Bind(
            "Experimental",
            "World Map Scale",
            2000f,
            new ConfigDescription(
                "How much the world is shrunk by: 2000 means 1:2000, which turns an 82 km map "
                + "into a 41 m model. Smaller numbers give a bigger model with more relief and "
                + "less of it in view at once. This is the control that decides whether you are "
                + "looking at a table or flying over a landscape.",
                new AcceptableValueRange<float>(200f, 20000f)));

        EyeHeight = config.Bind(
            "Experimental",
            "World Map Eye Height",
            12f,
            new ConfigDescription(
                "How far below you the model's sea level sits, in real metres. This is your "
                + "altitude over the model, and together with the scale it is what sets how much "
                + "stereo depth you get: too far and it flattens into a picture, too close and "
                + "you are inside the terrain.",
                new AcceptableValueRange<float>(1f, 100f)));

        ReliefExaggeration = config.Bind(
            "Experimental",
            "World Map Relief Exaggeration",
            1f,
            new ConfigDescription(
                "Vertical stretch applied to the model. At 1:2000 a 2 km mountain is 1 m tall, "
                + "which is true but hard to read; every relief map ever printed exaggerates. "
                + "1 is honest terrain.",
                new AcceptableValueRange<float>(1f, 10f)));

        HideCockpit = config.Bind(
            "Experimental",
            "World Map Hide Cockpit",
            true,
            "Take the cockpit out of view while the map is up. The point of this map is that you "
            + "are over the landscape, and a canopy rail across it says otherwise. The aircraft "
            + "keeps flying, and everything that is not the airframe — sky, weather, the real "
            + "world past the model — stays where it was.");

        DebugMarker = config.Bind(
            "Experimental",
            "World Map Debug Marker",
            false,
            "Put a plain 3 m cube on the model where the ground under the aircraft is. It uses "
            + "the engine's own material rather than the game's terrain shader, so it separates "
            + "'the model is not where I think it is' from 'the model is there and its shader "
            + "draws nothing'. Diagnostic only.");
    }
}
