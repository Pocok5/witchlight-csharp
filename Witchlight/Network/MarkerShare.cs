using System.Collections.Generic;
using ProtoBuf;

namespace Witchlight;

/// <summary>
/// Carries one player's marker to everyone else's client.
///
/// This holds no player uid. Clients need only the owner's name, and identity
/// stays on the server.
/// </summary>
[ProtoContract]
public class SharedMarker
{
    /// <summary>Identifies the marker, so a client can tell a new one from one it has.</summary>
    [ProtoMember(1)]
    public string Key { get; set; } = "";

    [ProtoMember(2)] public double X { get; set; }
    [ProtoMember(3)] public double Y { get; set; }
    [ProtoMember(4)] public double Z { get; set; }
    [ProtoMember(5)] public string Title { get; set; } = "";
    [ProtoMember(6)] public string Icon { get; set; } = Markers.PlainIcon;
    [ProtoMember(7)] public int Color { get; set; }
    [ProtoMember(8)] public string Owner { get; set; } = "";

    /// <summary>
    /// Says whether the player this is sent to keeps the marker in sight. This is
    /// the game's own pin, which holds a waypoint against the edge of the map
    /// rather than letting it scroll off. The answer belongs to one player, so it
    /// rides the per-player send rather than the marker itself.
    /// </summary>
    [ProtoMember(9)] public bool Pinned { get; set; }

    /// <summary>
    /// Says whether the player this is sent to may change the marker. The server
    /// decides, so the map can offer the fields or not without a client guessing
    /// what the server would allow.
    /// </summary>
    [ProtoMember(10)] public bool Editable { get; set; }
}

/// <summary>
/// Carries what a player asks of somebody else's marker from the in-game map.
///
/// This mod draws a shared marker rather than the game, so the game's own edit
/// window cannot reach it. That window names a waypoint by its place in the
/// asker's own list, and a marker that is not theirs has no such place. This
/// names the marker by key, which both halves agree on.
///
/// Every ask carries the pin, and one that is also an edit sets
/// <see cref="Editing"/>. The server decides whether the asker may do either.
/// </summary>
[ProtoContract]
public class SharedMarkerChange
{
    [ProtoMember(1)] public string Key { get; set; } = "";

    /// <summary>Says whether the asker keeps this marker in sight on their own map.</summary>
    [ProtoMember(2)] public bool Pinned { get; set; }

    /// <summary>Says whether the fields below are meant, or only the pin.</summary>
    [ProtoMember(3)] public bool Editing { get; set; }

    [ProtoMember(4)] public string Title { get; set; } = "";
    [ProtoMember(5)] public string Icon { get; set; } = Markers.PlainIcon;
    [ProtoMember(6)] public int Color { get; set; }

    /// <summary>Says whether the asker keeps the marker's name, picture and
    /// colour as a preset of their own, for the block it was made on.</summary>
    [ProtoMember(7)] public bool KeepPreset { get; set; }
}

/// <summary>Carries every marker one player should see from everyone else.</summary>
[ProtoContract]
public class SharedMarkers
{
    [ProtoMember(1)]
    public List<SharedMarker> Markers { get; set; } = new();
}
