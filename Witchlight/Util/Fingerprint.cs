using System;
using System.Linq;
using System.Text;
using Vintagestory.API.Common;

namespace Witchlight;

/// <summary>
/// Reduces what a palette depends on to a string.
///
/// The fingerprint covers the block registry and nothing else: game version,
/// then every block's id and code. The server sends its registry to clients on
/// join, so a connected client computes the same value. That is what makes the
/// fingerprint a shared token between the two.
///
/// Mod lists play no part. A client has client-side mods the server never hears
/// of, and the server has server-side ones the client never receives, so a
/// fingerprint covering them could not agree across the wire.
/// <see cref="LocalStamp"/> covers what that would have caught.
/// </summary>
public static class Fingerprint
{
    public static string Of(ICoreAPI api)
    {
        var builder = new StringBuilder();
        builder.Append(GameVersionOf()).Append('|');

        foreach (var block in api.World.Blocks)
        {
            if (block?.Code is not null)
            {
                builder.Append(block.Id).Append('=').Append(block.Code).Append(';');
            }
        }

        return Fnv1a.Of(builder.ToString());
    }

    /// <summary>
    /// Hashes the mod set as this machine sees it. This value is never sent
    /// anywhere. The server keeps it beside its palette so that a mod updating
    /// its textures invalidates the palette. Such an update changes colours
    /// without moving a single block id.
    /// </summary>
    public static string LocalStamp(ICoreAPI api)
    {
        var builder = new StringBuilder();
        foreach (var mod in api.ModLoader.Mods
                     .Where(mod => mod?.Info?.ModID is not null)
                     .OrderBy(mod => mod.Info.ModID, StringComparer.Ordinal))
        {
            builder.Append(mod.Info.ModID).Append(':').Append(mod.Info.Version).Append(';');
        }
        return Fnv1a.Of(builder.ToString());
    }

    private static string GameVersionOf()
    {
        return Vintagestory.API.Config.GameVersion.ShortGameVersion;
    }

}
