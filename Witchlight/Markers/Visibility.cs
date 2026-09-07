using System.Collections.Generic;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace Witchlight;

/// <summary>
/// Stores whether each marker is its owner's alone.
///
/// A waypoint has no field for this, because in the game a waypoint is only ever
/// its owner's. Sharing is this mod's idea, so this mod keeps the choice, in the
/// savegame beside the waypoints. <see cref="Beside{T}"/> owns the reading, the
/// writing and the forgetting.
///
/// Stores only a choice somebody actually made. A marker nobody has decided about
/// falls back to <c>allow_public_markers</c>, so the store holds one entry per
/// decision and stays empty on a server where nobody uses the web form.
/// </summary>
public sealed class Visibility
{
    private const string SaveKey = "witchlight:markervisibility";
    private const string Called = "marker visibility";

    private readonly Beside<bool> _chosen;

    private Visibility(Beside<bool> chosen)
    {
        _chosen = chosen;
    }

    /// <summary>An empty store, in which every marker takes the operator's
    ///  default. The mod holds this before the world is up.</summary>
    public static Visibility Empty => new(Beside<bool>.Empty(SaveKey));

    /// <summary>How many markers somebody has decided about. Reported by status.</summary>
    public int Decisions => _chosen.Count;

    /// <summary>Reads back what a previous run stored.</summary>
    public static Visibility Read(ICoreServerAPI api) =>
        new(Beside<bool>.Read(api, SaveKey, Called));

    /// <summary>Writes the choices, when they are not the ones already stored.</summary>
    public void Write(ICoreServerAPI api, IEnumerable<Waypoint>? alive) =>
        _chosen.Write(api, alive, Called);

    /// <summary>
    /// Returns true when this marker is its owner's alone. Falls back to the
    /// operator's setting for a marker nobody has decided for.
    /// </summary>
    public bool IsPrivate(Waypoint waypoint, bool byDefault) =>
        _chosen.Knows(waypoint.Guid, out var chosen) ? chosen : byDefault;

    /// <summary>Records what somebody chose for one marker.</summary>
    public void Choose(string guid, bool keepToOwner) => _chosen.Say(guid, keepToOwner);
}
