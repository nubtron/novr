namespace NOVR.VrMap;

/// <summary>
/// Which way the model is turned, and — much more importantly — what it is
/// turned *with*.
///
/// <para>The overlay room the model hangs in is not attached to the aircraft: it
/// sits near the world origin and the head inside it is the raw headset pose. So
/// a model left alone in that room is fixed to the physical room you are sitting
/// in, and flying the aircraft does not disturb it. Anything that ties the model
/// to the aircraft's attitude has to be asked for, and every degree of it is a
/// degree the model moves while you are trying to read it.</para>
/// </summary>
public enum WorldMapOrientation
{
    /// <summary>
    /// North stays north. The model does not move when the aircraft does — not
    /// when it rolls, not when it pitches, not when it turns — so it behaves like
    /// a table you are standing over rather than something strapped to the
    /// airframe. You look around it by moving your head, which is the one motion
    /// that should move a map.
    /// </summary>
    NorthUp,

    /// <summary>
    /// The aircraft's heading points away from you, so what is ahead on the model
    /// is ahead through the canopy. Only the heading: roll and pitch are dropped,
    /// which is what makes the difference between a map that turns when you turn
    /// and one that tips every time you move the stick. It still swings during a
    /// turn, which is the price of the alignment.
    /// </summary>
    TrackUp,

    /// <summary>
    /// The model carries the aircraft's whole attitude, so it stays fixed to the
    /// world while your head — which is in the aircraft — rolls and pitches
    /// against it. Geometrically the most honest and the worst to be inside:
    /// every stick input tips the map. Kept because it is what the first flights
    /// used and because it is the correct choice if you are stationary.
    /// </summary>
    WorldFixed
}
