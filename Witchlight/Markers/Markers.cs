using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace Witchlight;

/// <summary>
/// The one place that knows what a waypoint is, as the rest of this mod needs it.
///
/// Answers where the waypoint layer is, what identifies one waypoint, what its
/// packed colour is in CSS, and how to add, change or remove one. Every caller is
/// a thin call against these.
/// </summary>
public static class Markers
{
    /// <summary>
    /// Returns the layer every waypoint lives on, or null on a server whose map
    /// manager is not up.
    ///
    /// Looked up each time rather than held. Mods load in an order this does not
    /// choose, and a null answer on one tick is answered by the next.
    /// </summary>
    public static WaypointMapLayer? Layer(ICoreAPI api)
    {
        return api.ModLoader
            .GetModSystem<WorldMapManager>()?
            .MapLayers?
            .OfType<WaypointMapLayer>()
            .FirstOrDefault();
    }

    /// <summary>
    /// The name the game gives a marker nobody named. The map's own form uses the
    /// same word.
    /// </summary>
    public const string Unnamed = "Marker";

    /// <summary>The longest a marker's name may be.</summary>
    public const int LongestTitle = 128;

    /// <summary>The picture the game draws a marker with when nobody chose one.</summary>
    public const string PlainIcon = "circle";

    /// <summary>The CSS colour for a marker whose own colour could not be read.</summary>
    public const string WhiteHex = "#ffffff";

    /// <summary>The same fallback colour, packed as a waypoint stores it.</summary>
    public static readonly int White = Packed(WhiteHex)!.Value;

    /// <summary>
    /// Returns a marker's name, trimmed to <see cref="MostName"/> and falling back
    /// to <see cref="Unnamed"/>.
    ///
    /// Both paths that make a marker, the map's own form and a press of the key in
    /// game, call this, so they cannot disagree about the length or the word.
    /// </summary>
    public static string Title(string? said)
    {
        var name = (said ?? "").Trim();
        if (name.Length == 0)
        {
            return Unnamed;
        }
        return name.Length > LongestTitle ? name[..LongestTitle] : name;
    }

    /// <summary>
    /// Returns the picture to draw a marker with, or <see cref="DefaultIcon"/>.
    ///
    /// Trims the name. A picture name with a space around it is not a file on
    /// anybody's disk, and every caller goes through here so they cannot disagree
    /// about that.
    /// </summary>
    public static string Picture(string? icon) =>
        string.IsNullOrWhiteSpace(icon) ? PlainIcon : icon.Trim();

    /// <summary>
    /// Returns the identity anything outside the game knows a marker by.
    ///
    /// Uses the waypoint's own guid where there is one, and its position and title
    /// where there is not, which is enough to keep one marker from reading as two.
    /// </summary>
    public static string Key(Waypoint waypoint)
    {
        if (!string.IsNullOrEmpty(waypoint.Guid))
        {
            return waypoint.Guid;
        }

        return $"{(int)waypoint.Position.X}:{(int)waypoint.Position.Y}:{(int)waypoint.Position.Z}:{waypoint.Title}";
    }

    /// <summary>
    /// Converts a waypoint's packed colour to CSS.
    ///
    /// The game packs a waypoint colour as ARGB, with red in the high bytes. Every
    /// path that makes a waypoint ends in <c>Color.ToArgb</c> or
    /// <c>ColorUtil.Hex2Int</c>, and the game draws one by reading red back out of
    /// bit 16. Reading the low byte as red swaps red and blue, which leaves grey
    /// and green looking right and everything else wrong.
    /// </summary>
    public static string Hex(int color)
    {
        return $"#{ColorUtil.ColorR(color):x2}{ColorUtil.ColorG(color):x2}{ColorUtil.ColorB(color):x2}";
    }

    /// <summary>
    /// Converts CSS back to what a waypoint stores, always opaque. A waypoint with
    /// no alpha draws as nothing.
    ///
    /// Returns null for anything that is not six hex digits behind a hash, since
    /// what arrives here came from a browser.
    /// </summary>
    public static int? Packed(string? css)
    {
        if (css is null || css.Length != 7 || css[0] != '#')
        {
            return null;
        }

        if (!int.TryParse(css.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
        {
            return null;
        }

        return rgb | unchecked((int)0xff000000);
    }

    /// <summary>
    /// Returns the colours the game offers for a waypoint, in the order its own
    /// picker shows them.
    ///
    /// Reads them off the layer rather than listing them here, so a mod that adds a
    /// colour adds it to the web map's picker too. The game ships three entries
    /// missing their hash, which <c>Hex2Int</c> parses to some other colour, and
    /// these come back as whatever the game itself would draw. A picker that
    /// disagrees with the game is worse than one that repeats its mistake.
    /// </summary>
    public static List<string> Palette(ICoreAPI api)
    {
        var colors = Layer(api)?.WaypointColors;
        if (colors is null)
        {
            return new List<string>();
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var palette = new List<string>();
        foreach (var color in colors)
        {
            var hex = Hex(color);
            if (seen.Add(hex))
            {
                palette.Add(hex);
            }
        }
        return palette;
    }

    /// <summary>
    /// Returns true when this player may change this marker.
    ///
    /// The owner always may. Anybody else may only where the operator set
    /// <c>allow_editing_public_markers</c> and only for a marker that is in fact
    /// public. A private marker is never anybody's but its owner's whatever the
    /// setting says.
    ///
    /// The page answers the same question to decide whether to offer an edit. That
    /// answer is an affordance and this one is the gate. What the page believes
    /// about who owns what came from a post that may be seconds old, so this
    /// decides again against the waypoint itself.
    /// </summary>
    public static bool MayEdit(Waypoint waypoint, string uid, bool isPrivate, bool publicEditable)
    {
        if (string.IsNullOrEmpty(uid))
        {
            return false;
        }
        if (waypoint.OwningPlayerUid == uid)
        {
            return true;
        }
        return publicEditable && !isPrivate;
    }

    /// <summary>
    /// Returns the name of a marker's owner.
    ///
    /// Looks an offline owner up in the player data, so a marker still says whose
    /// it is when they are not on. The web feed and the in-game share both call
    /// this.
    /// </summary>
    public static string OwnerName(ICoreServerAPI api, string? uid)
    {
        if (string.IsNullOrEmpty(uid))
        {
            return "";
        }

        return api.World.PlayerByUid(uid)?.PlayerName
            ?? api.PlayerData.GetPlayerDataByUid(uid)?.LastKnownPlayername
            ?? "";
    }

    /// <summary>Returns the waypoint with this guid, or null when there is none.</summary>
    public static Waypoint? ByGuid(ICoreAPI api, string guid)
    {
        var waypoints = Layer(api)?.Waypoints;
        if (waypoints is null || string.IsNullOrEmpty(guid))
        {
            return null;
        }

        foreach (var waypoint in waypoints.ToList())
        {
            if (waypoint?.Guid == guid)
            {
                return waypoint;
            }
        }
        return null;
    }

    /// <summary>
    /// Sends a player their whole set of waypoints again.
    ///
    /// The layer sends a set rather than one waypoint, and the only call it offers
    /// is the one it makes when a player's map view moves. From the client's point
    /// of view that is what has happened whenever the server changed one of theirs.
    /// An offline owner is sent nothing and needs nothing, since the layer resends
    /// on their next view change.
    ///
    /// Both changing and removing a marker go through this.
    /// </summary>
    public static void Resend(ICoreServerAPI api, string? ownerUid)
    {
        if (Layer(api) is not { } layer)
        {
            return;
        }
        if (api.World.PlayerByUid(ownerUid ?? "") is IServerPlayer owner)
        {
            layer.OnViewChangedServer(owner, 0, 0, 0, 0);
        }
    }

    /// <summary>
    /// Changes a waypoint that already exists and resends it to its owner.
    ///
    /// Keeps the guid, so nothing that knows this marker loses track of it and a
    /// browser recognises its own edit arriving.
    /// </summary>
    public static void Change(
        ICoreServerAPI api,
        Waypoint waypoint,
        Vec3d position,
        string title,
        string icon,
        int color)
    {
        waypoint.Position = position;
        waypoint.Title = title;
        waypoint.Icon = icon;
        waypoint.Color = color;
        Resend(api, waypoint.OwningPlayerUid);
    }

    /// <summary>
    /// Removes a waypoint from the map and from its owner's. Returns true when
    /// something was removed.
    ///
    /// Only the owner may, whatever <c>public_markers_editable</c> says. That
    /// setting lets somebody correct a marker they can see, which is not the same
    /// permission as taking it off the map of the player who made it, and there is
    /// no way back from a removal.
    ///
    /// The next save drops the stored decision about who could see it.
    /// </summary>
    public static bool Remove(ICoreServerAPI api, Waypoint waypoint, string uid)
    {
        if (string.IsNullOrEmpty(uid) || waypoint.OwningPlayerUid != uid)
        {
            return false;
        }

        var layer = Layer(api);
        if (layer?.Waypoints is null || !layer.Waypoints.Remove(waypoint))
        {
            return false;
        }

        Resend(api, uid);
        return true;
    }

    /// <summary>
    /// Adds a new waypoint to the server's map and returns it.
    ///
    /// Adds an online owner's through the layer's own add, which resends their set
    /// so it appears on the map they have open. Adds an offline owner's to the list
    /// directly, and the layer resends their waypoints on their next map view
    /// change. Either way it is written to the savegame with the rest.
    /// </summary>
    /// <param name="guid">
    /// The guid to make the waypoint under. The service names a marker asked for on
    /// the web before the game has heard of it, and the browser watching for it to
    /// appear has only that name to match on.
    /// </param>
    public static Waypoint? Make(
        ICoreServerAPI api,
        string guid,
        string ownerUid,
        Vec3d position,
        string title,
        string icon,
        int color,
        bool pinned)
    {
        var layer = Layer(api);
        if (layer?.Waypoints is null)
        {
            return null;
        }

        var waypoint = new Waypoint
        {
            Position = position,
            Title = title,
            Icon = icon,
            Color = color,
            OwningPlayerUid = ownerUid,
            Pinned = pinned,
            Guid = guid,
        };

        if (api.World.PlayerByUid(ownerUid) is IServerPlayer online)
        {
            layer.AddWaypoint(waypoint, online);
        }
        else
        {
            layer.Waypoints.Add(waypoint);
        }

        return waypoint;
    }
}
