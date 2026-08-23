using HarmonyLib;

namespace NOVR.VrUi.HarmonyPatches;

/// <summary>
/// Stops the aeroplane being flown by the stick the cursor is being pointed
/// with.
///
/// <para><b>Why it is needed.</b> The stick cursor reads the game's own
/// actions, and on a gamepad — and on a HOTAS whose "Stick Cursor Axes" pair
/// is <c>Flight</c> — the pair it reads sits on the same physical stick as
/// "Roll"/"Pitch". Nothing in the game knows the mod has borrowed it, so
/// opening the map and moving the cursor onto an icon rolled and pitched the
/// aeroplane the whole way there.</para>
///
/// <para><b>Why the game does not already do this.</b> It does, for its own
/// pointer: <c>PilotPlayerState.PlayerAxisControls</c> freezes pitch, roll and
/// yaw whenever the virtual joystick is enabled and the map is maximized,
/// because there the mouse that flies the aircraft is also the mouse that
/// points at the map. The raw axes get no such treatment — in the flat game a
/// stick player has a mouse for the map and wants to keep flying — and that is
/// exactly the assumption VR breaks.</para>
///
/// <para><b>What it does not touch.</b> Yaw, the throttle, the brake and every
/// button: only the two axes the cursor took. And it is asked per frame, so
/// closing the map or handing the stick back to the map's own scroll gives the
/// aeroplane its stick back with it.</para>
/// </summary>
[HarmonyPatch(typeof(global::PilotPlayerState), "PlayerAxisControls")]
internal static class StickCursorFlightAxesPatch
{
    // Declared on the base type, which is where the field lives: reflection
    // does not hand out a base class's non-public fields through the derived
    // one.
    private static readonly AccessTools.FieldRef<global::PilotBaseState, global::ControlInputs> ControlInputsRef =
        AccessTools.FieldRefAccess<global::PilotBaseState, global::ControlInputs>("controlInputs");

    /// <summary>
    /// A postfix rather than a prefix: the method also runs the throttle and
    /// the G-LOC blackout, both of which should carry on happening while the
    /// map is up. Only the result is overruled.
    /// </summary>
    [HarmonyPostfix]
    private static void Postfix(global::PilotPlayerState __instance)
    {
        if (!VrUiCursor.StickCursorHoldsFlightAxes) return;

        // The virtual joystick's accumulators too, not just this frame's
        // output: PlayerAxisControls holds those values rather than rebuilding
        // them whenever the map is maximized, so a deflection left in them
        // would go on flying the aeroplane for as long as the map is up.
        __instance.pitchInput = 0f;
        __instance.rollInput = 0f;

        var inputs = ControlInputsRef(__instance);
        if (inputs == null) return;

        inputs.pitch = 0f;
        inputs.roll = 0f;
    }
}
