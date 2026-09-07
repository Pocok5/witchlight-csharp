using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace Witchlight;

/// <summary>
/// One marker somebody asked for on the web map, whether to make or to change.
///
/// Carries the whole marker either way. The form holds all of it, and a patch of
/// only what differs would make the mod work out what "differs" meant against a
/// marker somebody else may have moved since.
///
/// The service mints the key before the game has heard of the marker, and the game
/// makes the waypoint under that same key. The browser that asked has to recognise
/// its own marker arriving among everyone else's, and a name agreed at the moment
/// of asking is the only thing both ends can match on beforehand.
/// </summary>
public class Wanted
{
    /// <summary>The guid the waypoint is, or will be, made under.</summary>
    public string Key { get; set; } = "";

    /// <summary>The uid of the marker's owner, taken from their session.</summary>
    public string Uid { get; set; } = "";

    public string Title { get; set; } = "";
    public string Icon { get; set; } = Markers.PlainIcon;
    public string Color { get; set; } = Markers.WhiteHex;
    public int X { get; set; }
    public int Y { get; set; }
    public int Z { get; set; }

    /// <summary>True when its owner asked to keep it to themselves.</summary>
    public bool Private { get; set; }

    /// <summary>
    /// Which block this marker is about, where the map says so. Carries the code of
    /// the block it was put on, or the pattern of a preset it has been made to look
    /// like. Empty in the ordinary case, and the mod then reads the world under it.
    /// See <see cref="Origins"/>.
    /// </summary>
    public string Block { get; set; } = "";

    /// <summary>The marker's description, or empty. Trimmed, so whitespace and
    ///  silence are the same answer.</summary>
    public string About => Block.Trim();

    /// <summary>Where the game puts it, in the middle of the block named.</summary>
    public Vec3d Position => new(X + 0.5, Y, Z + 0.5);

    /// <summary>The colour as a waypoint stores it, falling back for anything that
    /// is not six hex digits behind a hash.</summary>
    public int Packed(int fallback) => Markers.Packed(Color) ?? fallback;

    /// <summary>The picture to draw it with, or the game's own default.</summary>
    public string Picture => Markers.Picture(Icon);

    /// <summary>The marker's name, falling back to the game's word for an unnamed
    ///  waypoint.</summary>
    public string Named => Markers.Title(Title);
}

/// <summary>
/// One marker somebody asked to be removed.
///
/// Carries a key and who asked, and nothing else. A removal names a waypoint
/// rather than describing one, and the mod reads the waypoint itself before
/// removing anything.
/// </summary>
public class Unwanted
{
    /// <summary>The guid of the waypoint to remove.</summary>
    public string Key { get; set; } = "";

    /// <summary>The uid of the player who asked, taken from their session.</summary>
    public string Uid { get; set; } = "";
}

/// <summary>
/// One marker somebody asked to pin, or to unpin.
///
/// Carries a key, who asked, and which way. Nothing about the marker itself, since
/// a pin changes what one player's own map shows rather than the marker. See
/// <see cref="Pins"/>.
/// </summary>
public class Pinning
{
    /// <summary>The guid of the waypoint to pin.</summary>
    public string Key { get; set; } = "";

    /// <summary>The uid of the player whose map it is for, taken from their
    ///  session.</summary>
    public string Uid { get; set; } = "";

    /// <summary>True to pin it, false to unpin it.</summary>
    public bool On { get; set; }
}

/// <summary>The marker requests the service was holding.</summary>
public class AskedMarkers
{
    public List<Wanted> Make { get; set; } = new();
    public List<Wanted> Change { get; set; } = new();
    public List<Unwanted> Remove { get; set; } = new();
    public List<Pinning> Pin { get; set; } = new();

    public bool Anything =>
        Make.Count > 0 || Change.Count > 0 || Remove.Count > 0 || Pin.Count > 0;
}

/// <summary>
/// Everything the service was holding when the mod last collected.
///
/// Carries markers and claims in one envelope, grouped by kind. The mod collects
/// on the tick that already posts positions, and a second queue would be a second
/// round trip every two seconds to find nothing in it.
/// </summary>
public class Asked
{
    public AskedMarkers Markers { get; set; } = new();
    public AskedClaims Claims { get; set; } = new();
}

/// <summary>How many requests of each kind landed. The caller logs all of them.</summary>
public readonly record struct Landed(
    int Made, int Changed, int Removed, int Pinned, Claimed Claims)
{
    /// <summary>True when anything at all landed.</summary>
    public bool Anything => AnyMarkers || Claims.Anything;

    /// <summary>True when a marker landed, which means the feed has to be shared
    ///  again. A claim the game has taken tells every client itself.</summary>
    public bool AnyMarkers => Made > 0 || Changed > 0 || Removed > 0 || Pinned > 0;
}

/// <summary>
/// Applies what players asked for on the web map.
///
/// The channel between the halves runs one way, from the mod that started the
/// service to the service it started, so the service cannot push a marker at the
/// game. A marker typed into the web form waits in the service until the mod
/// collects it on the tick that already posts positions.
///
/// The service has already checked what arrives here, and this checks it again. A
/// waypoint that will not draw is worse on the game's map than a form that
/// refused.
/// </summary>
public static class Pending
{
    /// <summary>
    /// Applies everything in the service's reply: the markers made, changed,
    /// removed and pinned, and the land claims drawn. Returns how many of each
    /// landed.
    ///
    /// Hands the claims straight to <see cref="Claiming"/>, which owns every rule
    /// about whether one may exist. This method is the envelope and the counting.
    /// </summary>
    public static Landed Apply(
        ICoreServerAPI api, Visibility visibility, Pins pins, Origins origins, string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return default;
        }

        Asked? asked;
        try
        {
            asked = JsonConvert.DeserializeObject<Asked>(json);
        }
        catch (Exception error)
        {
            api.Logger.Warning("[witchlight] could not read what the map is holding: {0}", error.Message);
            return default;
        }

        if (asked is null)
        {
            return default;
        }

        return new Landed(
            Made(api, visibility, origins, asked.Markers.Make),
            Changed(api, visibility, origins, asked.Markers.Change),
            Removed(api, asked.Markers.Remove),
            Kept(api, visibility, pins, asked.Markers.Pin),
            Claiming.Apply(api, asked.Claims));
    }

    /// <summary>
    /// Applies the markers somebody asked to pin or unpin.
    ///
    /// Requires only that the player may see the marker, which is a lower bar than
    /// changing one. A pin puts a marker on the pinner's map and on nobody else's,
    /// so anybody the marker is shared with may pin it, and a player's own is
    /// always theirs. Decides against the waypoint itself, because what the service
    /// believes about who owns what came from a post that is seconds old.
    /// </summary>
    private static int Kept(
        ICoreServerAPI api, Visibility visibility, Pins pins, List<Pinning> asked)
    {
        var byDefault = Settings.MarkersPrivateByDefault;

        var kept = 0;
        foreach (var pin in asked)
        {
            if (pin is null || string.IsNullOrEmpty(pin.Key) || string.IsNullOrEmpty(pin.Uid))
            {
                continue;
            }

            var waypoint = Markers.ByGuid(api, pin.Key);
            if (waypoint is null)
            {
                api.Logger.Notification(
                    "[witchlight] the map asked to pin a marker that is not there any more");
                continue;
            }

            if (waypoint.OwningPlayerUid != pin.Uid && visibility.IsPrivate(waypoint, byDefault))
            {
                api.Logger.Notification(
                    "[witchlight] {0} may not pin the marker \"{1}\"", pin.Uid, waypoint.Title);
                continue;
            }

            pins.Choose(api, waypoint, pin.Uid, pin.On);
            kept++;
        }

        return kept;
    }

    /// <summary>
    /// Applies the markers somebody asked to remove.
    ///
    /// <see cref="Markers.Remove"/> decides whether they may, against the waypoint
    /// itself, and only the owner ever may. A marker that is already gone is not
    /// logged as a failure, since two browsers open on one marker send the same
    /// removal twice.
    /// </summary>
    private static int Removed(ICoreServerAPI api, List<Unwanted> asked)
    {
        var removed = 0;
        foreach (var gone in asked)
        {
            if (gone is null || string.IsNullOrEmpty(gone.Key) || string.IsNullOrEmpty(gone.Uid))
            {
                continue;
            }

            var waypoint = Markers.ByGuid(api, gone.Key);
            if (waypoint is null)
            {
                continue;
            }

            if (!Markers.Remove(api, waypoint, gone.Uid))
            {
                api.Logger.Notification(
                    "[witchlight] {0} may not delete the marker \"{1}\"", gone.Uid, waypoint.Title);
                continue;
            }
            removed++;
        }

        return removed;
    }

    /// <summary>Makes the new markers, each owned by whoever the service says asked
    ///  for it.</summary>
    private static int Made(
        ICoreServerAPI api, Visibility visibility, Origins origins, List<Wanted> asked)
    {
        var made = 0;
        foreach (var wanted in asked)
        {
            if (wanted is null || !Sound(api, wanted))
            {
                continue;
            }

            var waypoint = Markers.Make(
                api,
                wanted.Key,
                wanted.Uid,
                wanted.Position,
                wanted.Named,
                wanted.Picture,
                wanted.Packed(Markers.White),
                pinned: false);

            if (waypoint is null)
            {
                api.Logger.Warning("[witchlight] no waypoint layer, so a marker the map asked for was dropped");
                continue;
            }

            // Record the choice whichever way it went. The operator's setting is
            // the fallback for a marker nobody decided about, and somebody filling
            // in this form decided, including by agreeing with it.
            visibility.Choose(waypoint.Guid, wanted.Private);
            origins.Made(waypoint.Guid, About(api, wanted));
            made++;
        }

        return made;
    }

    /// <summary>
    /// Applies the changes to markers that already exist.
    ///
    /// Decides whether somebody may here rather than taking the service's word. The
    /// service knows who owns what only from the mod's last post, which is seconds
    /// old and says nothing about a marker made or given away since. The waypoint
    /// itself is the only thing that knows now.
    /// </summary>
    private static int Changed(
        ICoreServerAPI api, Visibility visibility, Origins origins, List<Wanted> asked)
    {
        var byDefault = Settings.MarkersPrivateByDefault;
        var editable = Settings.PublicMarkersEditable;

        var changed = 0;
        foreach (var edit in asked)
        {
            if (edit is null || string.IsNullOrEmpty(edit.Key) || string.IsNullOrEmpty(edit.Uid))
            {
                continue;
            }

            var waypoint = Markers.ByGuid(api, edit.Key);
            if (waypoint is null)
            {
                api.Logger.Notification(
                    "[witchlight] the map asked to change a marker that is not there any more");
                continue;
            }

            if (!Markers.MayEdit(waypoint, edit.Uid, visibility.IsPrivate(waypoint, byDefault), editable))
            {
                api.Logger.Notification(
                    "[witchlight] {0} may not change the marker \"{1}\"", edit.Uid, waypoint.Title);
                continue;
            }

            Markers.Change(
                api, waypoint, edit.Position, edit.Named, edit.Picture, edit.Packed(waypoint.Color));

            origins.Made(waypoint.Guid, About(api, edit));

            // Somebody who may change a marker may change who sees it. On a marker
            // they do not own that only ever goes public to public, since making it
            // private would take it off its owner's map.
            if (waypoint.OwningPlayerUid == edit.Uid)
            {
                visibility.Choose(waypoint.Guid, edit.Private);
            }
            changed++;
        }

        return changed;
    }

    /// <summary>
    /// Returns which block a marker is about: what the request says, or what is
    /// actually there.
    ///
    /// The map names the block when it knows one, either from a right click or from
    /// the pattern of a preset a screenful of markers was made to look like. Only
    /// the map can know the second. Where the request is silent the mod reads the
    /// world, which only the mod can do.
    ///
    /// Only which preset the map offers for this marker rests on the answer, so a
    /// page naming something odd costs that page its own presets. Silence must mean
    /// read rather than forget.
    /// </summary>
    private static string About(ICoreServerAPI api, Wanted wanted)
    {
        var said = wanted.About;
        return said.Length > 0 ? said : Marking.CodeUnder(api, wanted.X, wanted.Y, wanted.Z);
    }

    /// <summary>
    /// Returns true when this is a marker the game can be asked to make. Nothing
    /// downstream can invent a key or an owner.
    /// </summary>
    private static bool Sound(ICoreServerAPI api, Wanted wanted)
    {
        if (string.IsNullOrEmpty(wanted.Key) || string.IsNullOrEmpty(wanted.Uid))
        {
            api.Logger.Warning("[witchlight] the map asked for a marker with no name or no owner");
            return false;
        }
        return true;
    }

}
