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
    public static ConfigEntry<WorldMapDetail> Detail;
    public static ConfigEntry<bool> HideCockpit;
    public static ConfigEntry<bool> HideHelmetPanels;
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
            + "depth to give, and a model a few metres away does. It does not replace the game's map — "
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
            1200f,
            new ConfigDescription(
                "How much the world is shrunk by: 1200 means 1:1200, which turns an 82 km map "
                + "into a 68 m model. Smaller numbers give a bigger model with more relief and "
                + "less of it in view at once. This is the control that decides whether you are "
                + "looking at a table or flying over a landscape. It works with Eye Height: at "
                + "1:1200 and 8 m the model's edge sits about 13 degrees below the horizon, so it "
                + "fills the lower half of your view; at 1:2000 and 12 m the edge falls to 30 "
                + "degrees down and you have to look for it.",
                new AcceptableValueRange<float>(200f, 20000f)));

        EyeHeight = config.Bind(
            "Experimental",
            "World Map Eye Height",
            8f,
            new ConfigDescription(
                "How far below you the model's sea level sits, in real metres. This is your "
                + "altitude over the model, and together with the scale it is what sets how much "
                + "stereo depth you get: too far and it flattens into a picture, too close and "
                + "you are inside the terrain.",
                new AcceptableValueRange<float>(1f, 100f)));

        ReliefExaggeration = config.Bind(
            "Experimental",
            "World Map Relief Exaggeration",
            2f,
            new ConfigDescription(
                "Vertical stretch applied to the model. At 1:1200 a 2 km mountain is 1.7 m tall, "
                + "which is true but hard to read; every relief map ever printed exaggerates. "
                + "1 is honest terrain. Keep the peaks under Eye Height or you will fly into "
                + "them: at 1:1200 and 2x, a 2 km summit reaches 3.3 m against an 8 m eye.",
                new AcceptableValueRange<float>(1f, 10f)));

        Detail = config.Bind(
            "Experimental",
            "World Map Detail",
            WorldMapDetail.Surfaces,
            "How much of the map the model is made of. Terrain is the ground tiles alone, which "
            + "is cheapest but leaves the map see-through wherever there is asphalt: roads, city "
            + "surfaces and fields are separate meshes filling cut-outs in the tiles, and only "
            + "256 of the map's ~2700 renderers are tiles. Surfaces adds everything lying on the "
            + "ground and nothing standing on it, which is the one that looks like a map. "
            + "Everything adds the buildings too, at thousands of renderers — try it if you have "
            + "the frames for it. Changing this rebuilds the model.");

        HideCockpit = config.Bind(
            "Experimental",
            "World Map Hide Cockpit",
            true,
            "Take the cockpit out of view while the map is up. The point of this map is that you "
            + "are over the landscape, and a canopy rail across it says otherwise. The aircraft "
            + "keeps flying, and everything that is not the airframe — sky, weather, the real "
            + "world past the model — stays where it was.");

        HideHelmetPanels = config.Bind(
            "Experimental",
            "World Map Hide Helmet Panels",
            true,
            "Take the tactical map and the weapon/countermeasure readout off the helmet while the "
            + "3D map is up. Both are stacked in front of the same view the model fills, and the "
            + "small flat map of the ground is the one thing the big solid one makes redundant. "
            + "The rest of the helmet display — speed, altitude, bearing, horizon — stays, "
            + "because you are still flying. Both come back exactly as they were when you close "
            + "the map.");

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
