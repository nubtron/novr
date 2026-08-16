using HarmonyLib;
using UnityEngine;

namespace NOVR.VrMap;

/// <summary>
/// Moving the map: the thumbsticks first, and the flight controls as well.
///
/// <para><b>Two sources, because there are two kinds of pilot.</b> The
/// thumbsticks are the right gesture for a map you are looking down at, and they
/// are free — the game has no VR bindings at all, so nothing is being taken from
/// anyone. The flight axes are here as well because they cost nothing to read and
/// they cover everyone: whatever you have Pitch, Roll and Yaw bound to moves the
/// map too, HOTAS, gamepad or keyboard, without being told about. Both are summed,
/// which needs no arbitration because you only have one pair of hands.</para>
///
/// <para><b>Why taking the flight controls is safer than not, which is not
/// obvious.</b> Handing the stick to a map sounds like walking away from a flying
/// aircraft, and it is the opposite: what the aircraft receives while the map is
/// open is <i>zero</i> pitch, roll and yaw, and zero through the flight assist is
/// wings level. Leaving the controls connected is the dangerous option, because
/// the map is drawn with the cockpit and the outside world hidden — you are
/// already not flying, and every input you make to move the map is an input the
/// aeroplane also takes, blind. The throttle is deliberately left alone: it is not
/// a movement control, and cutting or firewalling it is not something a map should
/// do.</para>
///
/// <para><b>Where the interception happens.</b> <c>PilotPlayerState</c> reads
/// Pitch, Roll and Yaw in its fixed step and writes them into the aircraft's
/// <c>ControlInputs</c>. A postfix there is the one place where the values are
/// known to have just been written and nothing downstream has read them yet — so
/// the game's own reading of the player's bindings is what drives the map and the
/// aircraft is zeroed in the same breath, with nothing having to guess at
/// devices.</para>
///
/// <para><b>The map is moved in map metres.</b> Panning shifts which point of the
/// theatre sits under your head; turning spins the model about that same point,
/// because that is the pivot you are actually looking down; zooming changes how
/// much world a metre of room is worth, about the same point. All three are
/// cleared when the map closes, which makes close-and-reopen the recentre
/// gesture.</para>
/// </summary>
internal static class WorldMapControls
{
    /// <summary>Where the model is panned to, in map metres, relative to the aircraft.</summary>
    public static Vector2 Pan { get; private set; }

    /// <summary>How far the model is spun about the point under the head, in degrees.</summary>
    public static float Spin { get; private set; }

    /// <summary>
    /// How much closer the model has been pulled than the configured scale, as a
    /// multiplier: 2 means twice the size and half the theatre in view.
    /// </summary>
    public static float Zoom { get; private set; } = 1f;

    /// <summary>
    /// Bounds on <see cref="Zoom"/>. Wide enough to go from the whole theatre on a
    /// table to a single airfield at arm's length, and closed enough that a stick
    /// left leaning cannot put you inside the terrain or lose the model entirely.
    /// </summary>
    private const float MinZoom = 0.25f;
    private const float MaxZoom = 8f;

    /// <summary>Doublings of the model's size per second at full stick.</summary>
    private const float ZoomRate = 1.2f;

    /// <summary>
    /// How far the stick has to go to fire a snap turn, and how far back it has to
    /// come before it will fire again. Two thresholds rather than one because a
    /// thumbstick held at a corner wanders by a few percent, and a single one
    /// turns that wander into a stream of turns.
    /// </summary>
    private const float SnapPress = 0.6f;
    private const float SnapRelease = 0.35f;

    private static bool _capturing;
    private static bool _takesControls = true;

    /// <summary>Which way the stick was last seen held far enough to have snapped.</summary>
    private static int _snapHeld;

    /// <summary>
    /// True while the map is open and set to take the controls — read by the
    /// patch, which runs whether or not this mod's map exists.
    /// </summary>
    public static bool Capturing =>
        _capturing && _takesControls &&
        VrMapConfig.CaptureControls != null && VrMapConfig.CaptureControls.Value;

    /// <summary>
    /// The scale the model is actually drawn at: what the pilot configured,
    /// divided by however far they have zoomed in since.
    /// </summary>
    public static float MapScale
    {
        get
        {
            var configured = VrMapConfig.Scale != null ? VrMapConfig.Scale.Value : 1200f;
            return Mathf.Max(1f, configured / Mathf.Clamp(Zoom, MinZoom, MaxZoom));
        }
    }

    /// <summary>
    /// Called every frame the map is open, before the model is placed.
    /// </summary>
    /// <param name="takeControls">
    /// False while the game's own pointer UI is up. The thumbsticks still move the
    /// map — they are nobody else's — but the aeroplane keeps its stick, because
    /// the pilot is choosing an airbase rather than flying and the map is only
    /// sharing the view.
    /// </param>
    public static void Refresh(bool takeControls = true)
    {
        _capturing = true;
        _takesControls = takeControls;

        // The sticks work whether or not the map has the flight controls — they
        // are not the aeroplane's and there is nothing to hand back.
        var left = WorldMapStick.Left;
        var right = WorldMapStick.Right;

        var pitch = -left.y;
        var roll = left.x;
        // Negated: pushing the stick right should turn *you* to the right, which
        // means the model turns to the left under you. The first version turned
        // the model the way the stick went, which is what a flight reported as
        // "reversed left right" — and it is, for the same reason a rear-view
        // mirror is: the thing you are steering is the viewpoint, not the map.
        var yaw = -right.x;
        var zoom = right.y;

        if (Capturing)
        {
            var input = GameManager.playerInput;
            if (input != null)
            {
                pitch += input.GetAxis("Pitch");
                roll += input.GetAxis("Roll");
                yaw -= input.GetAxis("Yaw");
            }
        }

        var scale = MapScale;

        // Room metres per second times the scale is map metres per second, so the
        // map moves at the same apparent speed whatever it is shrunk by: at 1:1200
        // and 3 m/s the model slides past you at 3 m/s no matter how much theatre
        // that is.
        var pan = (VrMapConfig.PanSpeed != null ? VrMapConfig.PanSpeed.Value : 3f)
                  * scale * Time.unscaledDeltaTime;
        var turn = (VrMapConfig.TurnSpeed != null ? VrMapConfig.TurnSpeed.Value : 45f)
                   * Time.unscaledDeltaTime;

        // Pushing forward sends the map away from you, which is the same
        // relationship a paper map has with your hands. Pitch reads positive when
        // pulled, so it is negated on the way in.
        var forward = -pitch * pan;
        var right2 = roll * pan;

        // Panning happens along the model's own axes, so "away" stays away after
        // the model has been spun.
        var spun = Quaternion.Euler(0f, -Spin, 0f);
        var moved = spun * new Vector3(right2, 0f, forward);

        Pan += new Vector2(moved.x, moved.z);
        Turn(yaw, turn);

        // Geometric, not linear: a fixed number of doublings a second, so pulling
        // the model in from the whole theatre and pushing it back out take the
        // same time and neither end crawls.
        if (Mathf.Abs(zoom) > 0f)
        {
            Zoom = Mathf.Clamp(
                Zoom * Mathf.Pow(2f, ZoomRate * zoom * Time.unscaledDeltaTime), MinZoom, MaxZoom);
        }
    }

    /// <summary>
    /// Turn the model — in steps if a step size is set, smoothly if it is zero.
    ///
    /// <para><b>Why steps are the default.</b> Turning the world around a seated
    /// head at a steady rate is the classic way to make someone sick: the eyes
    /// report a rotation the inner ear does not, and the mismatch lasts exactly as
    /// long as the stick is held. A snap gives the same mismatch for one frame
    /// instead of several seconds, which is why every VR title that lets you turn
    /// offers it. The map is a model rather than a world, so this is milder here
    /// than it would be standing on the ground — but it is a model that fills the
    /// lower half of your view, and a flight asked for snaps by name.</para>
    ///
    /// <para>Fires once per push and re-arms only when the stick comes back below
    /// <see cref="SnapRelease"/>, so holding it over turns once rather than
    /// spinning.</para>
    /// </summary>
    private static void Turn(float yaw, float smoothStep)
    {
        var step = VrMapConfig.TurnStep != null ? VrMapConfig.TurnStep.Value : 30f;
        if (step <= 0f)
        {
            _snapHeld = 0;
            Spin += yaw * smoothStep;
            return;
        }

        if (Mathf.Abs(yaw) <= SnapRelease) _snapHeld = 0;
        if (Mathf.Abs(yaw) < SnapPress) return;

        var direction = yaw > 0f ? 1 : -1;
        if (_snapHeld == direction) return;

        _snapHeld = direction;
        Spin += direction * step;
    }

    /// <summary>Give the controls back, and forget where the map was pushed to.</summary>
    public static void Release()
    {
        _capturing = false;
        _takesControls = true;
        _snapHeld = 0;
        Pan = Vector2.zero;
        Spin = 0f;
        Zoom = 1f;
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
