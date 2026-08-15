using HarmonyLib;
using UnityEngine;

namespace NOVR.VrMap;

/// <summary>
/// While the map is up, the stick moves the map instead of the aeroplane.
///
/// <para><b>Why this is safer than not doing it, which is not obvious.</b>
/// Handing the flight controls to a map sounds like walking away from a flying
/// aircraft, and it is the opposite: what the aircraft receives while the map is
/// open is <i>zero</i> pitch, roll and yaw, and zero through the flight assist is
/// wings level. Leaving the controls connected is the dangerous option, because
/// the map is drawn with the cockpit and the outside world hidden — you are
/// already not flying, and every input you make to move the map is an input the
/// aeroplane also takes, blind. The throttle is deliberately left alone: it is
/// not a movement control, and cutting or firewalling it is not something a map
/// should do.</para>
///
/// <para><b>Where the interception happens.</b> <c>PilotPlayerState</c> reads
/// Pitch, Roll and Yaw in its fixed step and writes them into the aircraft's
/// <c>ControlInputs</c>. A postfix there is the one place where the values are
/// known to have just been written and nothing downstream has read them yet — so
/// the game's own reading of the player's bindings is what drives the map, HOTAS,
/// keyboard, gamepad or anything else the player has set up, and the aircraft is
/// zeroed in the same breath. Nothing has to guess at devices.</para>
///
/// <para><b>The map is moved in map metres.</b> Panning shifts which point of the
/// theatre sits under your head; turning spins the model about that same point,
/// because that is the pivot you are actually looking down. Both are cleared when
/// the map closes, which makes close-and-reopen the recentre gesture.</para>
/// </summary>
internal static class WorldMapControls
{
    /// <summary>Where the model is panned to, in map metres, relative to the aircraft.</summary>
    public static Vector2 Pan { get; private set; }

    /// <summary>How far the model is spun about the point under the head, in degrees.</summary>
    public static float Spin { get; private set; }

    private static bool _capturing;

    /// <summary>
    /// True while the map is open and set to take the controls — read by the
    /// patch, which runs whether or not this mod's map exists.
    /// </summary>
    public static bool Capturing =>
        _capturing &&
        VrMapConfig.CaptureControls != null && VrMapConfig.CaptureControls.Value;

    /// <summary>
    /// Called every frame the map is open, before the model is placed. Reads the
    /// axes the game has already resolved from the player's own bindings.
    /// </summary>
    public static void Refresh(float mapScale)
    {
        _capturing = true;
        if (!Capturing) return;

        var input = GameManager.playerInput;
        if (input == null) return;

        var pitch = input.GetAxis("Pitch");
        var roll = input.GetAxis("Roll");
        var yaw = input.GetAxis("Yaw");

        // Room metres per second times the scale is map metres per second, so the
        // map moves at the same apparent speed whatever it is shrunk by: at 1:1200
        // and 3 m/s the model slides past you at 3 m/s no matter how much theatre
        // that is.
        var pan = (VrMapConfig.PanSpeed != null ? VrMapConfig.PanSpeed.Value : 3f)
                  * mapScale * Time.unscaledDeltaTime;
        var turn = (VrMapConfig.TurnSpeed != null ? VrMapConfig.TurnSpeed.Value : 45f)
                   * Time.unscaledDeltaTime;

        // Pushing the stick forward sends the map away from you, which is the same
        // relationship a paper map has with your hands. Pitch reads positive when
        // pulled, so it is negated.
        var forward = -pitch * pan;
        var right = roll * pan;

        // Panning happens along the model's own axes, so "away" stays away after
        // the model has been spun.
        var spun = Quaternion.Euler(0f, -Spin, 0f);
        var moved = spun * new Vector3(right, 0f, forward);

        Pan += new Vector2(moved.x, moved.z);
        Spin += yaw * turn;
    }

    /// <summary>Give the controls back, and forget where the map was pushed to.</summary>
    public static void Release()
    {
        _capturing = false;
        Pan = Vector2.zero;
        Spin = 0f;
    }

    /// <summary>
    /// Zero the aircraft's stick the moment the player's own input has been read
    /// into it. Not a prefix: letting the original run is what makes this work
    /// with every binding the player has, and it also means that if this postfix
    /// ever fails to apply, the aircraft flies normally rather than not at all.
    /// </summary>
    [HarmonyPatch(typeof(PilotPlayerState), "PlayerAxisControls")]
    internal static class PlayerAxisControlsPatch
    {
        private static readonly System.Reflection.FieldInfo? Field =
            AccessTools.Field(typeof(PilotPlayerState), "controlInputs");

        private static bool _reported;

        [HarmonyPostfix]
        private static void Postfix(PilotPlayerState __instance)
        {
            if (!Capturing) return;

            var inputs = Field?.GetValue(__instance) as ControlInputs;
            if (inputs == null) return;

            if (!_reported)
            {
                _reported = true;
                Debug.Log($"[NOVR] World map has the controls: the aircraft was being given " +
                          $"pitch {inputs.pitch:F2} roll {inputs.roll:F2} yaw {inputs.yaw:F2}, " +
                          "and is being given zero instead until the map closes.");
            }

            inputs.pitch = 0f;
            inputs.roll = 0f;
            inputs.yaw = 0f;
        }
    }
}
