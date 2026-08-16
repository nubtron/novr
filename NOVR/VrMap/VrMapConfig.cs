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
    public static ConfigEntry<WorldMapButton> ToggleButton;
    public static ConfigEntry<WorldMapPlacement> Placement;
    public static ConfigEntry<float> WallWidth;
    public static ConfigEntry<float> WallDistance;
    public static ConfigEntry<bool> WallFollowsHead;
    public static ConfigEntry<bool> FollowGameMap;
    public static ConfigEntry<WorldMapOrientation> Orientation;
    public static ConfigEntry<float> Scale;
    public static ConfigEntry<float> EyeHeight;
    public static ConfigEntry<float> ReliefExaggeration;
    public static ConfigEntry<WorldMapDetail> Detail;
    public static ConfigEntry<bool> Sea;
    public static ConfigEntry<bool> Haze;
    public static ConfigEntry<float> HazeRange;
    public static ConfigEntry<float> HazeCeiling;
    public static ConfigEntry<float> HazeStrength;
    public static ConfigEntry<bool> Icons;
    public static ConfigEntry<float> IconSize;
    public static ConfigEntry<bool> IconMask;
    public static ConfigEntry<float> IconOpacity;
    public static ConfigEntry<bool> IconOverlay;
    public static ConfigEntry<bool> Pointer;
    public static ConfigEntry<bool> CaptureControls;
    public static ConfigEntry<float> PanSpeed;
    public static ConfigEntry<float> TurnSpeed;
    public static ConfigEntry<float> TurnStep;
    public static ConfigEntry<bool> HideCockpit;
    public static ConfigEntry<bool> HideWorld;
    public static ConfigEntry<bool> HideHelmetPanels;
    public static ConfigEntry<bool> DebugMarker;
    public static ConfigEntry<bool> SelfTest;

    public static void Bind(ConfigFile config)
    {
        Enabled = config.Bind(
            "Experimental",
            "World Map",
            true,
            "Make the 3D world map available. It is the whole map as a solid model of the real "
            + "terrain — the game's own ground, at its own colours — at model scale, close enough "
            + "that both eyes see it as a solid thing rather than a flat picture. That last part "
            + "is the entire reason it exists: a real map seen from 40 km has no depth to give, "
            + "and a model a few metres away does. By default it stands in front of you where the "
            + "game's map goes and replaces it, opening and closing with it; see World Map "
            + "Placement for the alternative, which is the theatre laid out below you instead. "
            + "Turning this off costs nothing while the map is closed.");

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
            "Keyboard shortcut that shows and hides the 3D world map. Kept as a second way in, "
            + "because a keyboard is a poor thing to reach for in a headset — the controller "
            + "button below is the one meant to be used.");

        ToggleButton = config.Bind(
            "Input",
            "World Map Button",
            WorldMapButton.LeftSecondary,
            "Controller button that shows and hides the 3D world map. Every button on a VR "
            + "controller is free: the game has no VR bindings at all, because it is not a VR "
            + "game — the controllers exist only inside this mod — so nothing has to be given up "
            + "to bind this, and nothing the game does can shadow it. The default is the left "
            + "hand's upper face button (Y on a Touch controller), chosen because the right hand "
            + "is the one pointing at the map and the trigger on either hand already means "
            + "'select'. Set it to None to use the keyboard shortcut alone.");

        Placement = config.Bind(
            "Experimental",
            "World Map Placement",
            WorldMapPlacement.Wall,
            "Where the model hangs, which decides what kind of instrument it is. Wall stands it "
            + "up in front of you, in the place the game's own map appears — a solid hologram of "
            + "the theatre with north up the wall and the terrain standing out of it towards you. "
            + "The flat map is hidden while it is up, because it is the same map; the cockpit and "
            + "the outside world are not, because you have not gone anywhere, and the aeroplane "
            + "keeps its stick. Table lays the theatre out below you as a diorama you fly over, "
            + "with the ground under the aircraft directly under your head — the cockpit and the "
            + "world are hidden and the aeroplane is given a level stick, because you are "
            + "somewhere else while you read it. Wall is the default: it is the map the game "
            + "already has, in the place it already is, with depth added.");

        WallWidth = config.Bind(
            "Experimental",
            "World Map Wall Width",
            3f,
            new ConfigDescription(
                "How wide the whole theatre is on the wall, in real metres, before any zoom. The "
                + "width is what is chosen here and the scale falls out of it, so an 82 km map and "
                + "a 200 km one both fill the same wall — which is what the flat map does, and is "
                + "what makes mirroring its zoom mean anything. Zooming in with the map keys "
                + "enlarges the model about its centre exactly as it enlarges the flat one; there "
                + "is no frame to clip against, so the parts you have zoomed past extend beyond "
                + "what you can see, which is what a hologram would do.",
                new AcceptableValueRange<float>(0.5f, 30f)));

        WallDistance = config.Bind(
            "Experimental",
            "World Map Wall Distance",
            3f,
            new ConfigDescription(
                "How far in front of you the wall stands, in real metres. This is the number that "
                + "decides whether it is a hologram or a picture: the eyes are 65 mm apart, so "
                + "depth is a ratio against this distance and it falls away fast. At 3 m a "
                + "mountain standing 10 cm out of the wall is plainly in front of it; at the 25 m "
                + "the flat map is projected to, the same mountain is a photograph. Near enough to "
                + "reach is the point — the model is drawn over the cockpit, so nothing in the "
                + "aircraft can be in the way of it.",
                new AcceptableValueRange<float>(1f, 30f)));

        WallFollowsHead = config.Bind(
            "Experimental",
            "World Map Wall Follows Head",
            false,
            "Whether the wall turns to stay in front of you. Off, it is pinned where you were "
            + "facing when the map opened and stays there while you look around it and lean into "
            + "it — which is what makes it a hologram rather than a screen, and is where the "
            + "stereo comes from, since a wall that moves with your head cannot move against it. "
            + "Close and reopen the map to re-hang it in front of you. On, it stays ahead of you "
            + "the way the game's flat map does, at the cost of everything leaning would tell you.");

        FollowGameMap = config.Bind(
            "Experimental",
            "World Map Follows The Game Map",
            true,
            "Whether the model is panned and zoomed by the game's own map controls. On, this "
            + "model shows whatever the flat map is showing: the map scroll keys move it, Zoom "
            + "View zooms it, dragging with the mouse drags it, and jumping the map jumps it — "
            + "not by reading the same keys and hoping to agree, but by reading where the flat map "
            + "ended up, so every way it can move works, including ones added later. It applies "
            + "only while the game's map is actually open full-size, which is the only time those "
            + "controls do anything; the rest of the time the thumbsticks drive. Off, the "
            + "thumbsticks and the flight axes always drive.");

        Orientation = config.Bind(
            "Experimental",
            "World Map Orientation",
            WorldMapOrientation.NorthUp,
            "Which way the model is turned, and what turns it. The room the model hangs in is "
            + "not attached to the aircraft, so a model left alone in it is fixed to the "
            + "physical room you are sitting in and flying does not disturb it. North Up is "
            + "exactly that: north stays north, and the map behaves like a table you are "
            + "standing over — you look around it by moving your head, which is the one motion "
            + "that ought to move a map. Track Up points the aircraft's heading away from you so "
            + "what is ahead on the model is ahead through the canopy, taking the heading only, "
            + "so roll and pitch do not tip it; it still swings when you turn. World Fixed "
            + "carries the whole attitude, which pins the map to the world and makes your head "
            + "roll against it on every stick input — geometrically the most honest and the "
            + "worst to be inside, unless you are stationary.");

        Scale = config.Bind(
            "Experimental",
            "World Map Scale",
            2400f,
            new ConfigDescription(
                "How much the world is shrunk by: 2400 means 1:2400, which turns an 82 km map "
                + "into a 34 m model. Smaller numbers give a bigger model with more relief and "
                + "less of it in view at once. This is the control that decides whether you are "
                + "looking at a table or flying over a landscape. "
                + "It only means anything alongside Eye Height, and the pair of them is what "
                + "sets how much the model moves when you move your head. Scale and Eye Height "
                + "halved together leave the framing identical and double the stereo: the model's "
                + "edge stays about 13 degrees below the horizon either way, but at 1:1200 and 8 m "
                + "the ground below you is 8 metres off and reads nearly flat, while at 1:2400 and "
                + "4 m it is a model on a table you can lean over. Halve both again for a smaller "
                + "one still; change one without the other and you are moving the model in or out "
                + "of your view instead.",
                new AcceptableValueRange<float>(200f, 20000f)));

        EyeHeight = config.Bind(
            "Experimental",
            "World Map Eye Height",
            4f,
            new ConfigDescription(
                "How far below you the model's sea level sits, in real metres. This is your "
                + "altitude over the model, and together with the scale it is what sets how much "
                + "stereo depth you get: too far and it flattens into a picture, too close and "
                + "you are inside the terrain. It is the half of the pair that does the work — "
                + "eye separation and head movement are both fixed real distances, so everything "
                + "that makes the model look solid is a ratio against this number. See Scale above "
                + "for how to move the two together.",
                new AcceptableValueRange<float>(1f, 100f)));

        ReliefExaggeration = config.Bind(
            "Experimental",
            "World Map Relief Exaggeration",
            1f,
            new ConfigDescription(
                "Vertical stretch applied to the model. 1 is honest terrain — the ground at the "
                + "same scale as everything else — and is the default because a stereo model does "
                + "not need the trick a printed relief map needs: on the table at 1:2400 a 2 km "
                + "mountain is 0.8 m tall against a 4 m eye, which is small on paper and perfectly "
                + "readable when both eyes can see it. The wall is the case where the trick earns "
                + "its keep. Fitting a whole 82 km theatre into 3 m is 1:27000, so the same "
                + "mountain stands 7 cm out of the wall — about 1.7 arcminutes of disparity at 3 m, "
                + "which is right at the edge of what stereo resolves. Raise this to 3 or 4 there "
                + "and the terrain is unmistakably solid; the ground is then a lie you have "
                + "chosen, which on a map read for shape rather than for height is a cheap one.",
                new AcceptableValueRange<float>(1f, 10f)));

        Detail = config.Bind(
            "Experimental",
            "World Map Detail",
            WorldMapDetail.Everything,
            "How much of the map the model is made of. Terrain is the ground tiles alone, which "
            + "is cheapest but leaves the map see-through wherever there is asphalt: roads, city "
            + "surfaces and fields are separate meshes filling cut-outs in the tiles, and only "
            + "256 of the map's ~2700 renderers are tiles. Surfaces adds everything lying on the "
            + "ground and nothing standing on it. Everything adds the buildings as well, which is "
            + "446 renderers against 2648 and what cities look like from the air. Measured on one "
            + "map, headless: median frame 6.3 ms without them and 8.8 ms with, against 8.7 ms "
            + "with the map shut — so the buildings cost about 2.5 ms and the map as a whole "
            + "still costs nothing, because what it hides while it is up is the real world. Drop "
            + "to Surfaces if your frames say otherwise. Changing this rebuilds the model.");

        Sea = config.Bind(
            "Experimental",
            "World Map Sea",
            true,
            "Put water under the model. The game's own sea cannot come across — it is a single "
            + "plane that follows the camera, with a shader that reads its textures by world "
            + "position, and none of that survives being shrunk — so the model gets its own, "
            + "painted with the map's ocean texture. Without it the ground simply stops at the "
            + "coast and you see through the gap. Changing this rebuilds the model.");

        Haze = config.Bind(
            "Experimental",
            "World Map Haze",
            true,
            "Put air between you and the far side of the model. Everything that tells you how far "
            + "away a hill is — haze thickening, contrast falling, the distance going blue — is "
            + "the atmosphere in the way, and shrinking the theatre shrinks that too: 40 km of "
            + "air becomes 33 metres of it, which does nothing. Stereo carries the near part of "
            + "the model and gives up on the far part, so the far part reads as a painted "
            + "backdrop and the whole thing looks flat. This puts the air back at the model's own "
            + "scale, in the game's own haze colour. The unit symbols are not affected — they are "
            + "annotations, not things in the air, and one that faded with distance would be lying "
            + "about how well you can see it. It is built out of flat layers of air lying on the "
            + "map rather than out of fog, because the game's terrain shader ignores the engine's "
            + "fog settings entirely: measured, an eightfold change in fog distance produced frames "
            + "identical to a tenth of a grey level. Flat layers are also what makes it thin with "
            + "height instead of only with distance — a peak stands out of the haze the valley "
            + "beside it is buried in. Only the model is affected: the real world outside is drawn "
            + "by a different camera and cannot see them.");

        HazeCeiling = config.Bind(
            "Experimental",
            "World Map Haze Ceiling",
            2500f,
            new ConfigDescription(
                "How high the model's air goes, in map metres. Below this the haze thickens "
                + "towards sea level the way real air does, so low ground goes soft first and "
                + "summits keep their edges; above it the sky is clear. 2500 m is about where the "
                + "haze of a real day gives out, and it leaves the map's higher ground standing "
                + "out of the murk rather than sitting under it. Raise it for a hazy day that goes "
                + "all the way up; lower it for a shallow layer with the peaks in clear air.",
                new AcceptableValueRange<float>(200f, 10000f)));

        HazeRange = config.Bind(
            "Experimental",
            "World Map Haze Range",
            60f,
            new ConfigDescription(
                "How far you can see through the model's air, in map kilometres — the horizontal "
                + "distance from the point under your head at which sea-level ground is as lost as "
                + "Haze Strength says, thickening smoothly the whole way out and leaving the ground "
                + "directly beneath you untouched. Set in map kilometres rather than metres of room "
                + "because that is the quantity that means something: '40 km of visibility' stays "
                + "true when the map is rescaled, and a fog distance in metres does not. Lower for "
                + "a hazy day and more sense of depth; higher for a clear one and a flatter model.",
                new AcceptableValueRange<float>(5f, 300f)));

        HazeStrength = config.Bind(
            "Experimental",
            "World Map Haze Strength",
            0.5f,
            new ConfigDescription(
                "How much of the ground is lost at the far end of the range: 0.5 means the "
                + "furthest terrain still shows through at half. Together with the range this is "
                + "deliberately much thinner than a real 60 km day, which would take about 70% of "
                + "the contrast out of ground only 20 km away — measured, and it looks like it: the "
                + "map goes blue and stops being a map. What is wanted here is enough air to say "
                + "which ridge is in front of which, and no more; two flights running have asked "
                + "for less of it. Raise it towards 1 for weather.",
                new AcceptableValueRange<float>(0.1f, 1f)));

        Icons = config.Bind(
            "Experimental",
            "World Map Icons",
            true,
            "Stand the units and airbases on the model. Which units appear, what symbol each gets, "
            + "what colour it is and how big it is are not decided here: they are read off the "
            + "game's own map icon for that unit, so tracking, spotting, radar returns and the "
            + "last-known position of a contact that has gone cold all behave exactly as they do "
            + "on the flat map, and an airbase stays the landmark it is there rather than becoming "
            + "one more dot among the tanks. What is added is height — a symbol sits at the "
            + "altitude in the same tracking record its position comes from, so it is exactly as "
            + "stale as the position under it, and never a live altitude under a stale place. "
            + "That is the one thing a flat map cannot show and the reason to have a solid one. "
            + "The symbols are drawn over the terrain rather than into it, and see-through rather "
            + "than solid: a map symbol annotates the ground, so a ridge in front of it does not "
            + "saw it in half and the ground stays readable underneath it.");

        IconSize = config.Bind(
            "Experimental",
            "World Map Icon Size",
            0.18f,
            new ConfigDescription(
                "How big an ordinary unit's symbol is, in real metres. Everything else is sized "
                + "relative to that by the same ratios the flat map uses, so an airbase comes out "
                + "about three times an aircraft and stays findable. They are a fixed size in the "
                + "room rather than on the map, so zooming the model in and out does not change "
                + "how readable they are — which also means this has to move with Eye Height to "
                + "keep looking the same. 0.18 m at a 4 m eye subtends what 0.35 m did at 8 m.",
                new AcceptableValueRange<float>(0.05f, 2f)));

        IconMask = config.Bind(
            "Experimental",
            "World Map Icon Mask",
            true,
            "Move each symbol's transparency out of its brightness and into its alpha channel. "
            + "The game's icon sprites do not hold their shape in alpha: an airbase's is a white "
            + "disc with a black aircraft and black runway bars painted on it, opaque throughout, "
            + "and the black is where you are meant to see the ground. That is coherent because "
            + "the game draws every icon additively, where black adds nothing — but drawn over a "
            + "solid model with ordinary blending the same sprite is a disc with a solid black "
            + "aircraft on it. Converting keeps the symbol the game intended without depending on "
            + "a blend that disappears against snow or sky. Turn it off to see the sprites raw.");

        IconOpacity = config.Bind(
            "Experimental",
            "World Map Icon Opacity",
            0.6f,
            new ConfigDescription(
                "How solid a symbol is. The game's icon sprites are filled rather than outlined — "
                + "an airbase's is 78% fully opaque, and the buildings have no sprite at all and "
                + "draw as a plain rectangle — which costs nothing on a flat map, where the symbol "
                + "sits on a picture of the ground, and turns each one into a slab on a solid "
                + "model. Below 1 the terrain reads through the symbol and it becomes the "
                + "annotation it is meant to be. 1 is the flat map's own, fully solid.",
                new AcceptableValueRange<float>(0.05f, 1f)));

        IconOverlay = config.Bind(
            "Experimental",
            "World Map Icon Overlay",
            true,
            "Draw the unit symbols over the model instead of into it. A symbol is an annotation, "
            + "and an annotation is not occluded by the thing it annotates: left to depth-test "
            + "normally, one standing behind a ridge is sawn in half by it and one at ground level "
            + "is half-buried, which reads as a solid object embedded in the terrain rather than a "
            + "marker on a map. Turn it off to see the difference, or if you would rather a symbol "
            + "behind a mountain stayed behind it. This changes the depth test and nothing else; "
            + "how much of the terrain reads through a symbol is Icon Opacity above.");

        CaptureControls = config.Bind(
            "Experimental",
            "World Map Takes The Controls",
            true,
            "While the map is up and you are flying it, pitch, roll and yaw move the map instead "
            + "of the aeroplane: push "
            + "to send the model away from you, roll to slide it sideways, yaw to spin it about "
            + "the point under your head. This is on top of the controller thumbsticks, which "
            + "always move the map and are not affected by this setting — left stick to slide, "
            + "right stick left and right to turn (a snap turn: see Turn Step), right stick "
            + "forward and back to zoom — because "
            + "they are nobody else's: the game has no VR bindings at all. The flight controls are "
            + "left alone while the game's own pointer is up, because then you are choosing an "
            + "airbase rather than flying and the map is only sharing the view. All of it is cleared "
            + "when the map closes, so closing and reopening recentres it on the aircraft. Taking "
            + "the flight controls is safer than the alternative rather "
            + "than braver: what the aircraft gets while the map is up is zero stick, and zero "
            + "through the flight assist is wings level — whereas leaving the controls connected "
            + "means every input you make to move the map is one the aeroplane also takes while "
            + "you cannot see out of it. The throttle is untouched, because it is not a movement "
            + "control and a map has no business with the engines. It reads whatever you have "
            + "Pitch, Roll and Yaw bound to, so a HOTAS, a gamepad and the keyboard all work "
            + "without being told about.");

        PanSpeed = config.Bind(
            "Experimental",
            "World Map Pan Speed",
            3f,
            new ConfigDescription(
                "How fast the stick slides the map, in real metres per second — so the model "
                + "moves past you at the same apparent speed however much the world is shrunk "
                + "by. At 3 m/s and 1:2400 that is 7.2 km of theatre a second, and about 11 "
                + "seconds from one edge of the map to the other.",
                new AcceptableValueRange<float>(0.2f, 20f)));

        TurnStep = config.Bind(
            "Experimental",
            "World Map Turn Step",
            30f,
            new ConfigDescription(
                "How far one flick of the stick turns the map, in degrees — a snap turn. Turning "
                + "the world around a seated head at a steady rate is the classic way to make "
                + "someone sick: your eyes report a rotation your inner ear does not, and the "
                + "argument lasts exactly as long as you hold the stick. A snap compresses it into "
                + "one frame, which is why every VR title that lets you turn offers it. It fires "
                + "once per push and re-arms when the stick comes back to centre, so holding it "
                + "over turns once rather than spinning. Set it to 0 for the old smooth turn, at "
                + "Turn Speed below.",
                new AcceptableValueRange<float>(0f, 90f)));

        TurnSpeed = config.Bind(
            "Experimental",
            "World Map Turn Speed",
            45f,
            new ConfigDescription(
                "How fast yaw spins the map, in degrees per second, about the point under your "
                + "head — which is the point you are looking down at, and so the one worth "
                + "turning around. Only used when Turn Step above is 0.",
                new AcceptableValueRange<float>(5f, 180f)));

        HideCockpit = config.Bind(
            "Experimental",
            "World Map Hide Cockpit",
            true,
            "Take the cockpit out of view while the map is up. The point of this map is that you "
            + "are over the landscape, and a canopy rail across it says otherwise. The aircraft "
            + "keeps flying, and everything that is not the airframe — sky, weather, the real "
            + "world past the model — stays where it was.");

        HideWorld = config.Bind(
            "Experimental",
            "World Map Hide World",
            true,
            "Take the real world out of view while the map is up, leaving the sky and the model. "
            + "The model is a disc of ground a few tens of metres across, and with the real "
            + "terrain still drawn past its edge you are looking at the same country twice at two "
            + "different scales, with a hard cut where one stops. Sky, sun and weather stay, so "
            + "you are still somewhere — just not in two places at once. Turn it off to keep the "
            + "world and accept the seam.");

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

        Pointer = config.Bind(
            "Experimental",
            "World Map Pointer",
            true,
            "Point at the model with the controller and select what you are pointing at. A ring "
            + "runs over the ground where you are aiming and closes around a symbol when you are "
            + "on one; the trigger then means what a click means on the flat map — a target "
            + "designated or dropped while you are flying, an airbase chosen when you are not. It "
            + "uses whichever hand already drives the cursor, and head gaze if that is what you "
            + "have set. Picking is by angle rather than by hitting the rectangle exactly, so a "
            + "big symbol is easy and a small one is still reachable.");

        SelfTest = config.Bind(
            "Experimental",
            "World Map Self Test",
            false,
            "Open and close the map on a timer and report what happened, instead of flying it "
            + "yourself. Two things cannot be seen any other way: what the map costs — it logs "
            + "mean, median, p95 and worst frame time for each state, so the open and closed "
            + "numbers come from one flight rather than two — and whether closing it puts "
            + "everything back, which it checks panel by panel and camera by camera and says so. "
            + "The frame times are the game's own under whatever runtime is present, which is the "
            + "cost of drawing the map and not the rate a headset is handed frames at. "
            + "Diagnostic: it will take the map off you every few seconds.");

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
