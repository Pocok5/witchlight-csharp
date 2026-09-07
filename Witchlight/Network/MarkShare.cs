using System.Collections.Generic;
using ProtoBuf;

namespace Witchlight;

/// <summary>
/// Asks the server to make a marker from in game.
///
/// The map's own form asks the service and waits for the marker to arrive. A
/// client asks the server directly, because it is already talking to it and the
/// server holds the waypoint. Neither path lets the sender say whose marker it
/// is. The owner is the player the packet arrived from, the same way the service
/// takes it from a session.
///
/// The server decides which preset applies. It is the side that can read the
/// block at a position and ask the map service what that player has kept.
/// </summary>
[ProtoContract]
public class MarkAsk
{
    /// <summary>Where the marker goes, in the world's own coordinates.</summary>
    [ProtoMember(1)] public double X { get; set; }
    [ProtoMember(2)] public double Y { get; set; }
    [ProtoMember(3)] public double Z { get; set; }

    /// <summary>
    /// Which block names the marker: the one being looked at, or the one
    /// underfoot.
    ///
    /// This differs from the position above. Somebody standing on a rock and
    /// pressing the key wants a marker where they stand, named after the rock,
    /// and the block at their feet is air. The server reads the block itself from
    /// these three numbers rather than taking a code from a client.
    /// </summary>
    [ProtoMember(4)] public int BlockX { get; set; }
    [ProtoMember(5)] public int BlockY { get; set; }
    [ProtoMember(6)] public int BlockZ { get; set; }

    /// <summary>
    /// Says whether a preset should decide what this marker is.
    ///
    /// The hotkey and the chat command send true, which means marking this the
    /// way the player has said this kind of thing is marked. Where no preset
    /// names the block, nothing is made and the reply says so, so the client can
    /// open a window.
    ///
    /// That window sends false, because it has already asked every question a
    /// preset would have answered.
    /// </summary>
    [ProtoMember(7)] public bool UsePreset { get; set; }

    [ProtoMember(8)] public string Title { get; set; } = "";
    [ProtoMember(9)] public string Icon { get; set; } = Markers.PlainIcon;
    [ProtoMember(10)] public string Color { get; set; } = "";

    /// <summary>
    /// Says who may see the marker, as one of <see cref="Mark"/>'s three answers.
    ///
    /// This is a number rather than a nullable bool, because an unsaid answer is a
    /// real third state. Flattening it to false made somebody's marker public on a
    /// server whose default is private.
    /// </summary>
    [ProtoMember(11)] public int Private { get; set; } = Mark.Unsaid;

    /// <summary>Says whether this is also kept as the preset for that block.</summary>
    [ProtoMember(12)] public bool KeepPreset { get; set; }

    /// <summary>The pattern the preset is kept against. Empty means the block code itself.</summary>
    [ProtoMember(13)] public string Pattern { get; set; } = "";
}

/// <summary>
/// Carries what became of a <see cref="MarkAsk"/>, and everything a window needs
/// to finish it.
///
/// One packet covers both outcomes. A marker made and a marker no preset names
/// are the same question answered, and the fields a window fills itself from are
/// the fields the marker would have been made with.
/// </summary>
[ProtoContract]
public class MarkReply
{
    /// <summary>Says whether a marker now exists.</summary>
    [ProtoMember(1)] public bool Made { get; set; }

    /// <summary>The message to show the player.</summary>
    [ProtoMember(2)] public string Said { get; set; } = "";

    /// <summary>Says whether the client should open its window on this.</summary>
    [ProtoMember(3)] public bool Yours { get; set; }

    [ProtoMember(4)] public double X { get; set; }
    [ProtoMember(5)] public double Y { get; set; }
    [ProtoMember(6)] public double Z { get; set; }
    [ProtoMember(7)] public int BlockX { get; set; }
    [ProtoMember(8)] public int BlockY { get; set; }
    [ProtoMember(9)] public int BlockZ { get; set; }

    /// <summary>What the block at that spot is called.</summary>
    [ProtoMember(10)] public string Block { get; set; } = "";
    [ProtoMember(11)] public string Pattern { get; set; } = "";

    [ProtoMember(12)] public string Title { get; set; } = "";
    [ProtoMember(13)] public string Icon { get; set; } = "";
    [ProtoMember(14)] public string Color { get; set; } = "";

    /// <summary>Who would see the marker, already resolved against this person's
    /// own default and the operator's. A window always has one answer to show and
    /// never <see cref="Mark.Unsaid"/>.</summary>
    [ProtoMember(15)] public int Private { get; set; }

    /// <summary>Says whether the window's "keep as preset" starts on, which is
    /// what this person set on the map for themselves.</summary>
    [ProtoMember(16)] public bool KeepPreset { get; set; }

    /// <summary>
    /// Everything this person has kept, for the window to offer as a starting
    /// point. This is sent only with a reply that opens the window, because the
    /// list is the bulk of the packet and a marker already made has no use for it.
    /// </summary>
    [ProtoMember(17)] public List<PresetOffer> Presets { get; set; } = new();
}

/// <summary>
/// Carries one preset as the window is offered it, describing what a marker made
/// from it would be.
///
/// The service's own <see cref="Preset"/> record carries the same five fields.
/// This is that record in the shape that travels, with privacy spelled the way
/// <see cref="Mark"/> spells it, so the window falls back the same way the server
/// does.
/// </summary>
[ProtoContract]
public class PresetOffer
{
    [ProtoMember(1)] public string Pattern { get; set; } = "";
    [ProtoMember(2)] public string Title { get; set; } = "";
    [ProtoMember(3)] public string Icon { get; set; } = "";
    [ProtoMember(4)] public string Color { get; set; } = "";
    [ProtoMember(5)] public int Private { get; set; } = Mark.Unsaid;
}

/// <summary>
/// Asks a player's own client to mark what they are looking at.
///
/// The game keeps client and server commands in separate registries with
/// different prefixes, so `.wl mark` and `/wl mark` are two commands with one
/// behaviour. Which block somebody is looking at exists only on their own
/// machine, so the server's copy asks that machine rather than guessing from
/// where they stand.
///
/// This packet carries no fields. The client works out everything it would say.
/// </summary>
[ProtoContract]
public class MarkNudge
{
}

/// <summary>
/// Defines the three answers to who may see a marker, as they travel.
///
/// Unsaid must be zero and nothing else may be. Protobuf omits a field holding
/// its type's default, and the far end fills in whatever its own property
/// initializer said, so any meaning given to zero cannot be told from silence.
/// Zero therefore carries the answer that already means silence, and an explicit
/// `public` survives the wire because it is a one.
/// </summary>
public static class Mark
{
    /// <summary>Nobody has said who may see it. A preset decides, and failing
    /// that this person's own default. See <see cref="MarkAsk.Private"/>.</summary>
    public const int Unsaid = 0;

    /// <summary>Everybody on the server may see it.</summary>
    public const int Public = 1;

    /// <summary>Only its owner may see it.</summary>
    public const int Private = 2;

    /// <summary>Reports whether a number means private, given what an unsaid one falls back to.</summary>
    public static bool IsPrivate(int said, bool byDefault) =>
        said == Unsaid ? byDefault : said == Private;

    /// <summary>Returns the number for a boolean, for the side that holds one.</summary>
    public static int Says(bool kept) => kept ? Private : Public;
}
