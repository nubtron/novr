namespace NOVR.VrMap;

/// <summary>
/// Where the model hangs, which decides what kind of instrument it is.
///
/// <para>These are not two ways of drawing the same thing. A table is somewhere
/// you go: the cockpit and the world are taken away, the aeroplane's stick is
/// taken with them, and you are standing over the theatre. A wall is something
/// you look at: it appears where the game's own map appears, the cockpit stays,
/// the aeroplane keeps its controls, and you are still flying. Everything else
/// in this feature follows from which of those it is.</para>
/// </summary>
public enum WorldMapPlacement
{
    /// <summary>
    /// A vertical relief map standing in front of you, in the place the game's
    /// flat map would be — a hologram of the theatre with north up the wall and
    /// the terrain standing out of it towards you. The flat map is hidden while
    /// it is up, because this is the same map; the cockpit is not, because you
    /// have not gone anywhere.
    /// </summary>
    Wall,

    /// <summary>
    /// The theatre laid out below you as a diorama you fly over, with the ground
    /// under the aircraft directly under your head. The cockpit and the outside
    /// world are hidden, so this is a place you go rather than a panel you read —
    /// and the aeroplane is given a level stick while you are there.
    /// </summary>
    Table
}
