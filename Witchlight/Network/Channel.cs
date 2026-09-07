using System;
using Vintagestory.API.Client;
using Vintagestory.API.Server;

namespace Witchlight;

/// <summary>
/// Defines the channel the two halves talk over and everything it carries.
///
/// Both sides must register the same message types in the same order. The game
/// numbers them by registration order and matches a packet to a reader by that
/// number, so a list differing by one entry makes every message after it read as
/// the wrong thing. Holding the list here keeps the two sides in step.
///
/// Registration goes by type rather than by generic argument, so both sides share
/// one list despite their channels being different types. Each side keeps its own
/// handlers.
/// </summary>
public static class Channel
{
    /// <summary>The channel's name, on both sides.</summary>
    public const string Name = "witchlight";

    /// <summary>
    /// Lists everything the channel carries, in the order both sides number them.
    ///
    /// Adding to the end is safe between builds of the same minor. Inserting into
    /// the middle renumbers everything after it and is a protocol change.
    /// </summary>
    private static readonly Type[] Carries =
    {
        typeof(SharedMarkers),
        typeof(PaletteRequest),
        typeof(PaletteTable),
        typeof(IconRequest),
        typeof(IconTable),
        typeof(PortraitRequest),
        typeof(PlayerPortrait),
        typeof(MarkAsk),
        typeof(MarkReply),
        typeof(MarkNudge),
        typeof(SharedMarkerChange),
    };

    /// <summary>Registers everything the channel carries, on the server.</summary>
    public static IServerNetworkChannel Carrying(this IServerNetworkChannel channel)
    {
        Register(channel);
        return channel;
    }

    /// <summary>Registers everything the channel carries, on the client.</summary>
    public static IClientNetworkChannel Carrying(this IClientNetworkChannel channel)
    {
        Register(channel);
        return channel;
    }

    /// <summary>
    /// Registers the list above, in order. Both public overloads call this,
    /// because each side's channel is its own type and each caller chains its own
    /// handlers off what comes back.
    /// </summary>
    private static void Register(INetworkChannel channel)
    {
        foreach (var carried in Carries)
        {
            channel.RegisterMessageType(carried);
        }
    }
}
