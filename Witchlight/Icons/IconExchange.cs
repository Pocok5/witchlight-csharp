using System;
using System.Collections.Generic;
using Vintagestory.API.Server;

namespace Witchlight;

/// <summary>
/// Collects the pictures a marker is drawn with from joining clients.
///
/// This is the only way the pictures ever arrive. A dedicated server's install
/// carries no SVG at all, and its `textures` directory is present and empty of
/// them.
///
/// **Anybody is asked, and anybody may add a picture the server does not have.
/// Only an admin may replace one it does.** The palette follows the same rule.
/// Without it, a server whose operator never joins in game would draw every
/// marker as a plain diamond forever.
/// </summary>
public sealed class IconExchange(ICoreServerAPI api, string exports)
{
    private readonly ICoreServerAPI _api = api;
    private readonly string _exports = exports;

    /// <summary>Returns how many icons are on disk for the map service to draw with.</summary>
    public int Count => Icons.Stored(_exports).Count;

    /// <summary>
    /// Asks a joining client for the icons the server does not have.
    ///
    /// Asks only for what is missing, so a mod adding one marker costs one icon
    /// rather than the whole set, and a server with the full set asks for nothing.
    /// That keeps asking everybody to one small packet per join.
    /// </summary>
    public void AskForMissing(IServerPlayer player) => Ask(player, Icons.Stored(_exports));

    /// <summary>
    /// Asks a client for every icon, not only the missing ones. This is the
    /// recovery when an icon on disk is wrong rather than absent.
    /// </summary>
    public void AskForAll(IServerPlayer player) => Ask(player, new List<string>());

    private void Ask(IServerPlayer player, List<string> have)
    {
        _api.Network.GetChannel(Channel.Name)
            .SendPacket(new IconRequest { Have = have }, player);
    }

    /// <summary>
    /// Takes marker pictures from a client and keeps the ones they add.
    ///
    /// Merges rather than replacing, as the palette does. A client only has the
    /// art for the mods it has installed, so two players with different mod sets
    /// cover more between them than either alone.
    ///
    /// **An admin may overwrite a picture. Anybody else may only add one.**
    /// Filters a non-admin's contribution to names the server does not have and
    /// bounds how many it takes, since a client inventing names could otherwise
    /// fill a disk.
    /// </summary>
    public void Accept(IServerPlayer player, IconTable table)
    {
        var trusted = player.HasPrivilege(Privilege.controlserver);
        var sent = IconTable.Assemble(new[] { table });
        var taking = trusted ? sent : OnlyNew(sent);

        var written = Icons.Accept(taking, _exports);
        if (written > 0)
        {
            _api.Logger.Notification(
                "[witchlight] {0} marker pictures from {1}{2} ({3} in total now)",
                written,
                player.PlayerName,
                trusted ? "" : " (not an admin, so only new ones)",
                Count);
        }
    }

    /// <summary>
    /// Returns the pictures a server may take from a non-admin: the ones it has
    /// no file for, up to <see cref="MostFromPlayers"/>.
    ///
    /// The count is what makes this safe rather than the names. A name is already
    /// reduced to characters that are safe in a path, but nothing stops a client
    /// inventing an unlimited number of them, and every one becomes a file.
    /// </summary>
    private List<(string Name, byte[] Svg)> OnlyNew(List<(string Name, byte[] Svg)> sent)
    {
        var have = new HashSet<string>(Icons.Stored(_exports), StringComparer.Ordinal);
        var taking = new List<(string, byte[])>();

        foreach (var (name, svg) in sent)
        {
            if (have.Contains(name) || have.Count + taking.Count >= MostIcons)
            {
                continue;
            }
            taking.Add((name, svg));
        }

        return taking;
    }

    /// <summary>
    /// The most marker pictures the server will hold from players who are not
    /// admins.
    ///
    /// A stock game draws markers with about forty and a heavy mod set with a few
    /// hundred, so this is well past any real set and well short of filling a
    /// disk. An admin is not held to it, since an admin can write to the map
    /// directory anyway.
    /// </summary>
    private const int MostIcons = 512;

}
