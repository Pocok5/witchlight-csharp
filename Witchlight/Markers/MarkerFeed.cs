using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace Witchlight;

/// <summary>One map marker, as its owner placed it.</summary>
public class LiveWaypoint
{
    public string Title { get; set; } = "";
    public string Icon { get; set; } = Markers.PlainIcon;
    public string Color { get; set; } = Markers.WhiteHex;
    public int X { get; set; }
    public int Y { get; set; }
    public int Z { get; set; }
    /// <summary>The owner's display name, empty when it cannot be resolved.</summary>
    public string Owner { get; set; } = "";

    /// <summary>
    /// The owner's uid, exactly as the waypoint stores it. It survives a rename and
    /// every sharing rule keys on it, so it travels even when the name does not.
    /// </summary>
    public string OwnerUid { get; set; } = "";

    /// <summary>
    /// The block this marker was put on, such as <c>game:rock-granite</c>, or empty
    /// when the mod has no record of it. Every marker made before the mod kept the
    /// answer has none. A preset made from this marker is keyed on it. See
    /// <see cref="Origins"/>.
    /// </summary>
    public string Block { get; set; } = "";

    /// <summary>
    /// The waypoint's own guid, which names this marker wherever it goes. A browser
    /// that asked for a marker watches for this to appear, so what it gets back is
    /// the marker it asked for rather than one that looks like it.
    /// </summary>
    public string Key { get; set; } = "";

    /// <summary>
    /// True when this marker is its owner's alone. The service decides who is sent
    /// it from this, and the page shows it on the marker so a player who marked
    /// something private can see that it took.
    /// </summary>
    public bool Private { get; set; }

}

/// <summary>
/// Every marker, arranged by who may see it.
///
/// Deciding who sees what needs the owner and their choice, and the mod knows both,
/// so the sorting happens in the mod. The service holds two lists it only hands
/// out: everybody's, and each player's own.
/// </summary>
public class LiveMarkers
{
    /// <summary>The colours the game offers, so the web form offers the same.</summary>
    public List<string> Colors { get; set; } = new();

    /// <summary>The markers anyone may see.</summary>
    public List<LiveWaypoint> Public { get; set; } = new();

    /// <summary>The markers only their owner may see, by that owner's uid.</summary>
    public Dictionary<string, List<LiveWaypoint>> Private { get; set; } = new();

    /// <summary>
    /// Which markers each player keeps in sight on their own map, by uid.
    ///
    /// Sorted by reader, as the private markers are, so the service hands each
    /// player their own rather than working out whose is whose.
    /// <see cref="Pins"/> is the one place that knows.
    /// </summary>
    public Dictionary<string, List<string>> Pins { get; set; } = new();
}

/// <summary>
/// Builds the marker feed the map service reads.
///
/// Waypoints live server-side in the world map manager, so this reads every
/// marker. It also decides which of them reach whom, because the service does not
/// read a waypoint and the mod knows both the owner and their choice.
/// </summary>
public static class MarkerFeed
{
    /// <summary>Serializes <see cref="Sorted"/> to the JSON the service reads.</summary>
    public static string Json(
        ICoreServerAPI api, Visibility visibility, Pins pins, Origins origins)
    {
        return JsonConvert.SerializeObject(Sorted(api, visibility, pins, origins));
    }

    /// <summary>

    /// Builds the feed: every marker, split into what anyone may see and what only
    /// its owner may.
    ///
    /// The colour list travels here rather than on a channel of its own. It is a
    /// few hundred bytes against a payload of tens of kilobytes, it changes only
    /// when the mod set does, and sending it with the markers gives a restarted
    /// service the palette back on the next post.
    /// </summary>
    public static LiveMarkers Sorted(
        ICoreServerAPI api, Visibility visibility, Pins pins, Origins origins)
    {
        // Take one snapshot of the list, the way `All` does. The pins are read off
        // the same waypoints the markers are, and iterating a list while the game
        // adds to it is what can go wrong here.
        var alive = Markers.Layer(api)?.Waypoints?.ToList() ?? new List<Waypoint>();
        var sorted = new LiveMarkers
        {
            Colors = Markers.Palette(api),
            Pins = pins.Everyones(alive),
        };
        foreach (var marker in All(api, visibility, origins))
        {
            if (!marker.Private)
            {
                sorted.Public.Add(marker);
                continue;
            }

            // A private marker with no owner can be shown to nobody. It should not
            // exist, so drop it.
            if (marker.OwnerUid.Length == 0)
            {
                continue;
            }

            if (!sorted.Private.TryGetValue(marker.OwnerUid, out var theirs))
            {
                theirs = new List<LiveWaypoint>();
                sorted.Private[marker.OwnerUid] = theirs;
            }
            theirs.Add(marker);
        }

        return sorted;
    }

    /// <summary>

    /// Returns every marker saved on the server. Public because `/witchlight
    /// status` reports how many there are. An empty map with a working service
    /// means either no markers or no post, and an operator needs to tell them
    /// apart.
    /// </summary>
    public static List<LiveWaypoint> All(
        ICoreServerAPI api, Visibility visibility, Origins origins)
    {
        var layer = Markers.Layer(api);
        if (layer?.Waypoints is null)
        {
            return new List<LiveWaypoint>();
        }

        // Read the setting each post rather than caching it, so an operator's
        // change takes effect on the next post instead of the next restart.
        var byDefault = Settings.MarkersPrivateByDefault;

        var waypoints = new List<LiveWaypoint>();
        foreach (var waypoint in layer.Waypoints.ToList())
        {
            if (waypoint?.Position is null)
            {
                continue;
            }

            waypoints.Add(new LiveWaypoint
            {
                Title = waypoint.Title ?? "",
                Icon = Markers.Picture(waypoint.Icon),
                Color = Markers.Hex(waypoint.Color),
                X = Blocks.At(waypoint.Position.X),
                Y = Blocks.At(waypoint.Position.Y),
                Z = Blocks.At(waypoint.Position.Z),
                Owner = Markers.OwnerName(api, waypoint.OwningPlayerUid),
                OwnerUid = waypoint.OwningPlayerUid ?? "",
                Block = origins.Of(waypoint.Guid),
                Key = Markers.Key(waypoint),
                Private = visibility.IsPrivate(waypoint, byDefault),
            });
        }
        return waypoints;
    }
}
