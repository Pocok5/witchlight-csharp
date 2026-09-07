using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace Witchlight;

/// <summary>One player's current position and card.</summary>
public class LivePlayer
{
    public string Name { get; set; } = "";
    public string Uid { get; set; } = "";

    /// <summary>
    /// How far the server loads ground around this player, in chunks. This is
    /// their granted view distance, capped at the server's MaxChunkRadius. It
    /// bounds what standing here adds to their map and how far beside them the
    /// map service may ask the game for ground.
    /// </summary>
    public int ViewChunks { get; set; }

    /// <summary>
    /// The name of this player's stored picture, or null when they have none.
    ///
    /// Carries the name rather than a URL, and <see cref="Portraits"/> works it
    /// out. A player uid is base64 and carries characters a path cannot, so the
    /// one place that files these decides what they are called.
    /// </summary>
    public string? Portrait { get; set; }

    /// <summary>
    /// When that picture was drawn, in seconds, or zero when there is none.
    ///
    /// The name is the same before and after a player is redrawn, so without this
    /// the map would go on showing the picture it already had until somebody
    /// reloaded the page. It travels beside the name because what is stored is one
    /// file under one name.
    /// </summary>
    public long PortraitAt { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public int Z { get; set; }

    /// <summary>
    /// Which way this player is looking, in degrees clockwise from north.
    ///
    /// A compass bearing, which is what a north-up map rotates a player marker by.
    /// The game holds a yaw in radians measured from south and turning the other
    /// way. <see cref="Bearing"/> converts it here, so nothing downstream has to
    /// know the game's convention.
    /// </summary>
    public float Facing { get; set; }

    /// <summary>
    /// This player's health and food, with the maximum of each.
    ///
    /// Both live in the entity's watched attributes on the server, so showing them
    /// asks nothing of the client. The maximums travel with the values because a
    /// player's can be raised.
    /// </summary>
    public float Health { get; set; }
    public float MaxHealth { get; set; }
    public float Saturation { get; set; }
    public float MaxSaturation { get; set; }

    /// <summary>
    /// The extra bars this server has been asked to show for a player, such as
    /// mana or a level.
    ///
    /// Empty both on a server that asked for none and on a player who has none.
    /// See <see cref="PlayerBars"/> for how the map reads a mod's numbers without
    /// knowing anything about the mod.
    /// </summary>
    public List<LiveBar> Bars { get; set; } = new();
}

/// <summary>One extra bar's value and maximum.</summary>
public class LiveBar
{
    public string Name { get; set; } = "";
    public float Value { get; set; }
    public float Max { get; set; }

    /// <summary>The colour to draw it, as the settings gave it.</summary>
    public string Colour { get; set; } = "";

    /// <summary>
    /// The group to file it under where the map lets a reader switch bars off, or
    /// empty when nothing could name one. See
    /// <see cref="PlayerBars.GroupFor"/> for why it cannot always be worked out.
    /// </summary>
    public string Group { get; set; } = "";
}

/// <summary>
/// Who is online, arranged by who may see them.
///
/// Travels in the same shape the markers do. A setting and the game's groups decide
/// whether one player's position may be shown to another, and the mod knows both,
/// so the sorting happens here and the service hands out the lists without looking
/// into them.
/// </summary>
public class LivePlayers
{
    /// <summary>
    /// How many players are online, sent to everybody.
    ///
    /// A server that hides where people are standing still reports how busy it is.
    /// That is a fact about the server rather than about anybody on it.
    /// </summary>
    public int Online { get; set; }

    /// <summary>The players anyone may see. Everybody, where positions are public.</summary>
    public List<LivePlayer> Public { get; set; } = new();

    /// <summary>
    /// The players each person may see beyond <see cref="Public"/>, by their uid.
    /// Empty where positions are public, since everybody is then already in
    /// <see cref="Public"/>.
    /// </summary>
    public Dictionary<string, List<LivePlayer>> Private { get; set; } = new();

    /// <summary>
    /// Who shares a group with each person, by uid.
    ///
    /// Does not decide who may be seen. It lets the page offer "my group" as a way
    /// of filtering a list it already has. Everybody shares a group with
    /// themselves, so a person with no groups still gets a list holding themselves.
    /// </summary>
    public Dictionary<string, List<string>> Grouped { get; set; } = new();

    /// <summary>
    /// Every group the server has, by id, with its name and its members, online or
    /// not. The service shares a map against this, so it has to name members who
    /// are offline. A player shares with their group rather than with whoever of it
    /// is logged in.
    /// </summary>
    public Dictionary<string, LiveGroup> Groups { get; set; } = new();
}

/// <summary>One group's id, name and members, as the service reads them.</summary>
public class LiveGroup
{
    public string Name { get; set; } = "";
    public List<string> Members { get; set; } = new();
}

/// <summary>
/// Builds the feed of who is online for the map service.
/// </summary>
public static class PlayerFeed
{
    /// <summary>Serializes <see cref="Sorted"/> to the JSON the service reads.</summary>
    public static string Json(ICoreServerAPI api, string exports)
    {
        return JsonConvert.SerializeObject(Seen(api, exports));
    }

    /// <summary>
    /// Builds the feed: who is online, split into what anybody may see and what
    /// only a group may.
    ///
    /// Where positions are public, everybody goes in one list. Where they are not,
    /// each person gets a list holding themselves and whoever the game has in a
    /// group with them.
    ///
    /// Builds a list for everybody in a group, online or not, rather than only for
    /// the players in the world. Somebody signed in to the map while their player
    /// is offline is still in their group. Memberships come from the server's
    /// player data through <see cref="AllGroups"/>, which names every member
    /// whether or not they are on.
    /// </summary>
    public static LivePlayers Seen(ICoreServerAPI api, string exports)
    {
        var players = All(api, exports);
        var sorted = new LivePlayers { Online = players.Count };

        // Read the setting each post rather than caching it, so an operator's
        // change takes effect on the next post rather than the next restart. The
        // markers follow the same rule.
        var everyones = Settings.PlayersPublic;
        if (everyones)
        {
            sorted.Public = players;
        }

        sorted.Groups = AllGroups(api);
        var groups = GroupsOf(sorted.Groups);

        // Everybody with a list to build: whoever is online, and whoever is in a
        // group.
        var everybody = players.Select(player => player.Uid)
            .Concat(groups.Keys)
            .Where(uid => uid.Length > 0)
            .Distinct(StringComparer.Ordinal);

        foreach (var uid in everybody)
        {
            var mine = groups.TryGetValue(uid, out var held) ? held : new HashSet<string>();
            var together = players
                .Where(other => other.Uid.Length > 0 && (other.Uid == uid
                    || (groups.TryGetValue(other.Uid, out var theirs) && theirs.Overlaps(mine))))
                .ToList();

            sorted.Grouped[uid] = together.Select(other => other.Uid).ToList();
            if (!everyones)
            {
                sorted.Private[uid] = together;
            }
        }

        return sorted;
    }

    /// <summary>
    /// Returns which groups each player is in, by uid, inverted from the group
    /// table.
    ///
    /// Built once per post rather than per pair. Comparing everybody with everybody
    /// is already a square of the players, and searching every group inside that
    /// loop would make it a cube.
    /// </summary>
    private static Dictionary<string, HashSet<string>> GroupsOf(Dictionary<string, LiveGroup> all)
    {
        var groups = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var (id, group) in all)
        {
            foreach (var uid in group.Members)
            {
                if (!groups.TryGetValue(uid, out var held))
                {
                    held = new HashSet<string>(StringComparer.Ordinal);
                    groups[uid] = held;
                }
                held.Add(id);
            }
        }
        return groups;
    }

    /// <summary>
    /// Returns every group the server has and everybody in it, read from the
    /// server's player data rather than from the players who are online.
    ///
    /// The game keeps a group's online members on the group and every player's
    /// memberships on the player, so the full membership comes from reading the
    /// second the other way round.
    /// </summary>
    private static Dictionary<string, LiveGroup> AllGroups(ICoreServerAPI api)
    {
        var all = new Dictionary<string, LiveGroup>();
        var known = api.Groups?.PlayerGroupsById;
        if (known is null)
        {
            return all;
        }

        foreach (var (id, group) in known)
        {
            if (group is null || !Joined(api, id))
            {
                continue;
            }
            all[id.ToString()] = new LiveGroup { Name = group.Name ?? "" };
        }

        var data = api.PlayerData?.PlayerDataByUid;
        if (data is null)
        {
            return all;
        }

        foreach (var (uid, player) in data)
        {
            foreach (var id in player?.PlayerGroupMemberships?.Keys ?? Enumerable.Empty<int>())
            {
                if (all.TryGetValue(id.ToString(), out var group))
                {
                    group.Members.Add(uid);
                }
            }
        }
        return all;
    }

    /// <summary>
    /// Returns true when a membership names a group somebody actually joined.
    ///
    /// A player's memberships are not only the groups they joined. The game puts
    /// every player in its own channels for general chat, server info and the
    /// damage and info logs, and those arrive as memberships like any other. Every
    /// pair of players therefore shares one, which made "my group" on the map show
    /// the whole server on any server that had never created a group.
    ///
    /// So a membership counts when the server can name a player group behind it.
    /// The game's own channels have none. This reads the server's group list rather
    /// than rejecting the uids those channels happen to use, since a rule about
    /// which numbers are real breaks quietly the day the game adds a channel.
    ///
    /// A group the game made to carry a private message is a real group and still
    /// does not count. It is two people talking, and reading it as a party would
    /// put whoever you last messaged on your group list and leave them there.
    /// </summary>
    private static bool Joined(ICoreServerAPI api, int group)
    {
        var known = api.Groups?.PlayerGroupsById;
        return known is not null
            && known.TryGetValue(group, out var joined)
            && joined is not null
            && !joined.CreatedByPrivateMessage;
    }

    /// <summary>
    /// Converts a game yaw to a compass bearing: degrees clockwise from north,
    /// under 360.
    ///
    /// The game's yaw is radians from south, turning anticlockwise, so zero looks
    /// south and a quarter turn looks east. That is how
    /// <c>BlockFacing.HorizontalFromYaw</c> reads it and how the client's own map
    /// rotates its player marker. The half turn and the change of sign are the
    /// difference between the two conventions.
    /// </summary>
    private static float Bearing(float yaw) => GameMath.Mod(180f - yaw * GameMath.RAD2DEG, 360f);

    /// <summary>
    /// Returns the bars this player carries, out of the ones the settings ask for.
    ///
    /// A player with none gets an empty list rather than a list of empty bars.
    /// <see cref="PlayerBars"/> distinguishes a bar that does not apply from one
    /// that is at zero.
    /// </summary>
    private static List<LiveBar> Bars(ICoreServerAPI api, Entity entity)
    {
        var found = new List<LiveBar>();
        foreach (var bar in PlayerBar.Settled(api))
        {
            if (bar.Of(entity) is { } reading)
            {
                found.Add(reading);
            }
        }
        return found;
    }

    /// <summary>
    /// Returns how many chunks the server loads around a player. The server grants
    /// a view distance in blocks and loads whole chunks out to it, no further than
    /// its own MaxChunkRadius.
    /// </summary>
    private static int ViewChunksOf(ICoreServerAPI api, IPlayer player)
    {
        var blocks = player.WorldData?.LastApprovedViewDistance ?? 0;
        var chunks = (int)Math.Ceiling(blocks / (double)Math.Max(1, api.WorldManager.ChunkSize));
        return Math.Max(0, Math.Min(chunks, api.Server.Config.MaxChunkRadius));
    }

    /// <summary>
    /// Returns everybody who is actually in the world.
    ///
    /// The game's list of online players also holds whoever is still connecting,
    /// as its own documentation warns. A connecting player already has an entity
    /// standing at their last position for the half minute or more their client
    /// takes to load, or indefinitely when it never finishes. Every reading of who
    /// is on goes through here, so the map does not report somebody as on when
    /// nobody is.
    /// </summary>
    private static IEnumerable<IServerPlayer> Playing(ICoreServerAPI api)
    {
        return api.World.AllOnlinePlayers
            .OfType<IServerPlayer>()
            .Where(player => player.ConnectionState == EnumClientState.Playing);
    }

    public static List<LivePlayer> All(ICoreServerAPI api, string exports)
    {
        var players = new List<LivePlayer>();
        foreach (var player in Playing(api))
        {
            if (player.Entity?.Pos is not { } position)
            {
                continue;
            }

            var watched = player.Entity.WatchedAttributes;
            var health = watched?.GetTreeAttribute("health");
            var hunger = watched?.GetTreeAttribute("hunger");
            var stored = Portraits.StoredFor(exports, player.PlayerUID ?? "");

            players.Add(new LivePlayer
            {
                Name = player.PlayerName ?? "",
                Uid = player.PlayerUID ?? "",
                ViewChunks = ViewChunksOf(api, player),
                X = Blocks.At(position.X),
                Y = Blocks.At(position.Y),
                Z = Blocks.At(position.Z),
                Facing = Bearing(position.Yaw),
                Health = health?.GetFloat("currenthealth") ?? 0f,
                MaxHealth = health?.GetFloat("maxhealth") ?? 0f,
                Saturation = hunger?.GetFloat("currentsaturation") ?? 0f,
                MaxSaturation = hunger?.GetFloat("maxsaturation") ?? 0f,
                Bars = Bars(api, player.Entity),
                Portrait = stored?.Name,
                PortraitAt = stored?.At ?? 0,
            });
        }
        return players;
    }
}
