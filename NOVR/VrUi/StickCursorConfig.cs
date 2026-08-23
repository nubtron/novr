using BepInEx.Configuration;

namespace NOVR.VrUi;

/// <summary>
/// The stick-driven UI cursor: a third way to move the VR cursor, alongside
/// the desktop mouse and the head-gaze and motion-controller modes.
///
/// <para>It is driven by the game's <i>own</i> Rewired actions rather than by
/// anything the mod binds: "Pan View"/"Tilt View" — the view axes, normally a
/// thumbstick or a hat, and dead in a VR cockpit because the head does the
/// looking. Nothing new to bind. "Move Map Horizontal"/"Move Map Vertical"
/// can drive it instead ("Stick Cursor Axes"), as can the flight axes or the
/// free camera's — because a gamepad usually puts two pairs on each stick,
/// and which pair is free depends on which stick the pilot needs for the
/// screen they are on. The mod cannot see sticks, only actions, so it reads
/// the bindings back out of Rewired and prints which stick each pair is on.</para>
///
/// <para>Over the maximized tactical map the stick goes back to the map for
/// the same reason ("Stick Cursor Over Map"), leaving the cursor where it
/// was — which is how the flat game already works for a pad player. Both
/// defaults come from one measurement rather than a survey: the saved
/// gamepad map on the machine this was flown on binds the view axes and the
/// map axes to the same stick.</para>
///
/// <para>Why it exists: head-gaze moves the cursor with the neck and is
/// measured from a head-relative reference, so it moves when the view is
/// recentred and it asks the pilot to look where they want to click. The
/// stick cursor is a position on the panel — it stays where it was left, it
/// survives a recentre, and the head is free to look elsewhere while it is
/// being aimed. It also needs no tracked controller, which is what makes a
/// HOTAS-only cockpit usable without reaching for the mouse.</para>
///
/// <para>It is the default mode. The head-gaze cursor it displaces asks the
/// pilot to aim with their neck and puts the cursor wherever they happen to
/// be looking, which is exactly what a pointer already under the thumb
/// avoids; the mouse is still one movement away in either mode.</para>
///
/// <para>The settings live here rather than in ModConfiguration because this
/// mod is developed as a stack of independent branches, and a setting declared
/// in the shared file puts every layer that adds one in conflict with every
/// other. See <see cref="ConfigSectionAttribute"/>.</para>
/// </summary>
[ConfigSection(Order = 50)]
public static class StickCursorConfig
{
    private const string Section = "General";

    public static ConfigEntry<bool> StickCursor;
    public static ConfigEntry<float> StickCursorSpeed;
    public static ConfigEntry<float> StickCursorDeadzone;
    public static ConfigEntry<StickCursorAxisSource> StickCursorAxes;
    public static ConfigEntry<bool> StickCursorOverMap;
    public static ConfigEntry<bool> StickCursorInvertVertical;
    public static ConfigEntry<float> StickCursorScrollSpeed;

    // Every read goes through these: ConfigSections catches a section that
    // fails to bind and carries on, so an entry can legitimately be null and
    // the cursor must keep working when it is.
    public static bool Enabled => StickCursor != null && StickCursor.Value;
    public static float Speed => StickCursorSpeed != null ? StickCursorSpeed.Value : 0.4f;
    public static float Deadzone => StickCursorDeadzone != null ? StickCursorDeadzone.Value : 0.15f;
    public static StickCursorAxisSource Axes =>
        StickCursorAxes != null ? StickCursorAxes.Value : StickCursorAxisSource.View;
    public static bool MoveOverMap => StickCursorOverMap != null && StickCursorOverMap.Value;
    public static bool InvertVertical => StickCursorInvertVertical != null && StickCursorInvertVertical.Value;
    public static float ScrollSpeed => StickCursorScrollSpeed != null ? StickCursorScrollSpeed.Value : 6f;

    public static void Bind(ConfigFile config)
    {
        StickCursor = config.Bind(
            Section,
            "Stick Cursor",
            true,
            "Move the VR cursor with the game's own view axes ('Pan View'/'Tilt View' — normally a thumbstick or hat), so menus and the map can be used without a mouse or a tracked controller. Unlike the head-gaze cursor it does not follow your head: it stays where you left it, it is unaffected by recentering, and you can look somewhere else while aiming it. The mouse still works — moving it takes the cursor back at once. On by default; while it is on it overrides both 'Head Gaze Cursor' and 'Cursor Input Source', so turn it off to fall back to whichever of those two is selected.");

        StickCursorSpeed = config.Bind(
            Section,
            "Stick Cursor Speed",
            0.4f,
            new ConfigDescription(
                "How fast the stick cursor travels, in screens per second at full stick deflection. 1.0 crosses the whole panel in one second, which flying it showed to be about three times too fast to place the cursor on a menu entry: the default is a panel crossed in two and a half seconds.",
                new AcceptableValueRange<float>(0.1f, 5f)));

        StickCursorDeadzone = config.Bind(
            Section,
            "Stick Cursor Deadzone",
            0.15f,
            new ConfigDescription(
                "Stick deflection ignored before the cursor starts moving. The game applies its own deadzone first; this one exists because a cursor that creeps is far more annoying than a view that creeps, and because the same axis may be a well-centred stick on one machine and a worn one on another.",
                new AcceptableValueRange<float>(0f, 0.5f)));

        StickCursorAxes = config.Bind(
            Section,
            "Stick Cursor Axes",
            StickCursorAxisSource.Camera,
            "Which of the game's own axis pairs moves the cursor. All four are actions you have "
            + "already bound, and the mod reads them rather than binding anything new — but a "
            + "gamepad usually puts *two* pairs on each stick, so the one to pick is whichever "
            + "pair sits on the stick you do not need for the screen you are on. Your log says "
            + "which is which: every time the stick cursor starts it prints one line per pair "
            + "with the controller elements Rewired has it on ('Left Stick X', 'Right Stick Y'), "
            + "and marks the ones that share a stick with the map.\n"
            + "Camera = 'Move Lateral'/'Move Longitudinal', the free camera's, and the default. "
            + "It is the one pair nothing reads in the cockpit — maximizing the map does not "
            + "change camera state, and the cockpit camera reads only 'Move Vertical' of the "
            + "three — so with the map open in flight the cursor is all this pair does, and the "
            + "map keeps its own scroll axes. The cost is on the ground: on the spawn map and in "
            + "the mission editor these fly the free camera, so the view drifts while you "
            + "point.\n"
            + "Flight = 'Roll'/'Pitch'. The pair every setup has bound — a stick, a pad's left "
            + "thumbstick, the keyboard's own WASD — and free wherever there is no aircraft, "
            + "which is every menu and the spawn map. The mirror image of Camera: the cost is "
            + "the cockpit, where opening the map and moving the cursor flies the aeroplane.\n"
            + "View = 'Pan View'/'Tilt View', the free-look axes, which the game ignores whenever "
            + "a cursor is up. Free in every screen — unless they share a stick with the map "
            + "scroll, which on a gamepad they usually do.\n"
            + "Map = 'Move Map Horizontal'/'Move Map Vertical'. Free everywhere except the "
            + "maximized map, which is the one place they scroll it.");

        StickCursorOverMap = config.Bind(
            Section,
            "Stick Cursor Over Map",
            false,
            "Keep moving the stick cursor while the tactical map is maximized, even when it is the same stick that scrolls the map. Off (the default) hands that stick to the map: the map scrolls under a cursor that stays where it is and 'Select' takes whatever is nearest it, which is exactly what the flat game does for a pad player — otherwise one stick scrolls the map and drags the cursor off the icon in the same motion. This only ever applies when 'Stick Cursor Axes' really is on the map's stick; pick a pair on your other stick and the cursor keeps moving over the map with nothing to stand down from. The mouse and a motion controller move the cursor there in every case.");

        StickCursorInvertVertical = config.Bind(
            Section,
            "Stick Cursor Invert Vertical",
            false,
            "Invert the stick cursor's vertical axis. Off means stick up moves the cursor up, whatever the flight view's own pitch inversion is set to — the cursor is a pointer, not a view.");

        StickCursorScrollSpeed = config.Bind(
            Section,
            "Stick Cursor Scroll Speed",
            6f,
            new ConfigDescription(
                "How fast the 'Zoom View' axis scrolls the list under the stick cursor, in mouse-wheel notches per second at full deflection. Only while flight controls are off (i.e. a menu is up) and the tactical map is not maximized, so it never fights the VR zoom or the map's own zoom. 0 disables it.",
                new AcceptableValueRange<float>(0f, 30f)));
    }
}

/// <summary>
/// Which of the game's own axis pairs drives the stick cursor. Named after the
/// actions rather than after a stick, because actions are all the mod can bind
/// to — <see cref="StickCursorConfig"/> logs which stick each one landed on.
/// </summary>
public enum StickCursorAxisSource
{
    /// <summary>"Pan View"/"Tilt View" — free whenever a cursor is up.</summary>
    View,
    /// <summary>"Roll"/"Pitch" — free wherever there is no aircraft to fly.</summary>
    Flight,
    /// <summary>"Move Lateral"/"Move Longitudinal" — the free camera's.</summary>
    Camera,
    /// <summary>"Move Map Horizontal"/"Move Map Vertical" — the map's own.</summary>
    Map,
}
