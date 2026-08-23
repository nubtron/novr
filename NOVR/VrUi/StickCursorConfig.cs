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
/// can drive it too ("Stick Cursor Map Axes"), but that is off by default:
/// the two action pairs usually sit on the same physical stick, so taking
/// both doubles one stick rather than adding a second.</para>
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
    public static ConfigEntry<bool> StickCursorMapAxes;
    public static ConfigEntry<bool> StickCursorOverMap;
    public static ConfigEntry<bool> StickCursorInvertVertical;
    public static ConfigEntry<float> StickCursorScrollSpeed;

    // Every read goes through these: ConfigSections catches a section that
    // fails to bind and carries on, so an entry can legitimately be null and
    // the cursor must keep working when it is.
    public static bool Enabled => StickCursor != null && StickCursor.Value;
    public static float Speed => StickCursorSpeed != null ? StickCursorSpeed.Value : 0.4f;
    public static float Deadzone => StickCursorDeadzone != null ? StickCursorDeadzone.Value : 0.15f;
    public static bool UseMapAxes => StickCursorMapAxes != null && StickCursorMapAxes.Value;
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

        StickCursorMapAxes = config.Bind(
            Section,
            "Stick Cursor Map Axes",
            false,
            "Also move the stick cursor with the map scroll axes ('Move Map Horizontal'/'Move Map Vertical'), everywhere except the maximized tactical map — there they still scroll the map, as they do in the flat game. Off by default because the two action pairs can share one physical stick — on the gamepad this was measured on, the saved map bound both to the same stick — and then this doubles that one stick's contribution instead of giving the cursor a second one. Turn it on if your view axes and your map axes are on different sticks.");

        StickCursorOverMap = config.Bind(
            Section,
            "Stick Cursor Over Map",
            false,
            "Keep moving the stick cursor while the tactical map is maximized. Off (the default) hands the stick to the map there: the map scrolls under a cursor that stays where it is and 'Select' takes whatever is nearest it, which is exactly what the flat game does for a pad player. That matters because the view axes and the map axes are often the same stick, so a cursor that also moves means every scroll drags the cursor off the icon you were aiming at. The mouse still moves the cursor over the map either way, and so does a motion controller.");

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
