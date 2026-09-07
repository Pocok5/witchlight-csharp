using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace Witchlight;

/// <summary>One rectangle of ground a claim covers, seen from above.</summary>
public class LiveArea
{
    public int X1 { get; set; }
    public int Z1 { get; set; }
    public int X2 { get; set; }
    public int Z2 { get; set; }

    /// <summary>
    /// The lowest and highest block the claim reaches.
    ///
    /// Nothing draws with these. The popup displays them, so a reader can tell a
    /// claim over three blocks of a cellar from one over the sky above it.
    /// </summary>
    public int Y1 { get; set; }
    public int Y2 { get; set; }
}

/// <summary>One person a claim lets in, and what it lets them do.</summary>
public class LiveGuest
{
    public string Uid { get; set; } = "";

    /// <summary>The name the game last knew them by. The form displays this name
    ///  and a player types it to name them.</summary>
    public string Name { get; set; } = "";

    /// <summary>True when they may build and break. False when they may only use
    ///  and walk. These are the two levels `/land claim grant` offers.</summary>
    public bool Builds { get; set; }
}

/// <summary>
/// One land claim, as the map draws it.
///
/// Carries the claim's areas rather than its bounding box. A claim is built from
/// adjacent rectangles, and their outline is the boundary. A box around the whole
/// claim would draw a fence around ground nobody has taken.
/// </summary>
public class LiveClaim
{
    /// <summary>
    /// The name that identifies this claim to the web map.
    ///
    /// The game gives a land claim no id of its own, so <see cref="ClaimFeed"/>
    /// derives this from the claim's owner and the ground it covers. A page sends
    /// it back to name the claim it wants changed or given up.
    ///
    /// The key changes when the claim's ground changes. A page holding a key for
    /// a claim somebody has since redrawn is refused, rather than editing
    /// whatever now sits on that land.
    /// </summary>
    public string Key { get; set; } = "";

    /// <summary>The owner's name, as the game last knew it.</summary>
    public string Owner { get; set; } = "";

    /// <summary>
    /// The owner's uid, which survives a rename.
    ///
    /// The page compares it against the viewer, so a player who has just drawn a
    /// claim sees their own appear.
    /// </summary>
    public string OwnerUid { get; set; } = "";

    /// <summary>The name the owner gave it, or empty when they gave it none.</summary>
    public string Description { get; set; } = "";

    /// <summary>The ground it covers, in one rectangle or several.</summary>
    public List<LiveArea> Areas { get; set; } = new();

    /// <summary>The players it lets in beyond its owner.</summary>
    public List<LiveGuest> Guests { get; set; } = new();

    /// <summary>True when anybody may use what is on the claim, and true when
    ///  anybody may walk across it. These are the only two permissions the game
    ///  grants to everybody rather than to a named player.</summary>
    public bool EveryoneUses { get; set; }
    public bool EveryoneWalks { get; set; }

    /// <summary>The volume of land the claim takes, which is what an allowance is
    ///  spent on. Read from the game rather than recomputed, so the map and the
    ///  game agree about what a claim costs.</summary>
    public int Volume { get; set; }
}

/// <summary>
/// What one person is allowed to claim, and what they have already used.
///
/// Sent to the web map so its form can state what a rectangle costs before the
/// player asks for it. A survival player is allowed a quarter of a million cubic
/// metres, which at the full height of the world is a square thirty-two blocks
/// across, so a form without these numbers gives a refusal the player could not
/// predict.
///
/// These are the numbers the role and the player data gave at the moment the feed
/// was built. The mod checks all of them again against the claim it is handed, so
/// the form saves a round trip and never grants anything.
/// </summary>
public class ClaimAllowance
{
    /// <summary>Cubic metres this person may hold in total.</summary>
    public long Allowance { get; set; }

    /// <summary>Cubic metres their existing claims already come to.</summary>
    public long Used { get; set; }

    /// <summary>How many separate claims their role allows, and how many they hold.</summary>
    public int MaxAreas { get; set; }
    public int Areas { get; set; }

    /// <summary>The smallest area their role allows, on each axis.</summary>
    public int LeastX { get; set; }
    public int LeastY { get; set; }
    public int LeastZ { get; set; }

    /// <summary>
    /// Returns why this ground may not be claimed, or null when it may.
    ///
    /// Tests the three refusals in the order a player meets them: too many
    /// claims, too small, then past the volume they are allowed. The player is
    /// shown this wording, so each message names the number that stopped them.
    /// </summary>
    /// <param name="ground">The box the player wants to claim.</param>
    public string? Refuses(Cuboidi ground)
    {
        if (Areas >= MaxAreas)
        {
            return $"they already have {Areas} claims, which is all their role allows";
        }

        if (ground.SizeX < LeastX || ground.SizeY < LeastY || ground.SizeZ < LeastZ)
        {
            return $"{ground.SizeX}x{ground.SizeY}x{ground.SizeZ} is under the "
                + $"{LeastX}x{LeastY}x{LeastZ} their role allows";
        }

        var total = Used + ground.SizeXYZ;
        if (total > Allowance)
        {
            return $"that would bring them to {total}m³, past the {Allowance}m³ they are allowed";
        }

        return null;
    }
}

/// <summary>
/// Every land claim, with the uids of the players who may be told about them.
///
/// A privilege decides who may see claims, so the answer is one list of players
/// rather than a per-claim answer. The claims travel once with that list beside
/// them. Markers travel in a different shape because a marker is private to
/// whoever made it.
/// </summary>
public class LiveClaims
{
    /// <summary>
    /// True when anybody may see the claims, signed in or not.
    ///
    /// Set from the `[claims] view` privilege. Only the widest setting holds for
    /// a reader the mod has never heard of. Anything narrower is decided per
    /// person and listed in <see cref="Seen"/>.
    /// </summary>
    public bool Everyones { get; set; }

    /// <summary>
    /// How tall this world is, in blocks.
    ///
    /// Travels with the claims because the form needs it and a map drawn from
    /// above cannot show it. A claim's depth accounts for most of its volume, and
    /// this is the height a full-height claim takes.
    /// </summary>
    public int Height { get; set; }

    /// <summary>The claims themselves.</summary>
    public List<LiveClaim> Claims { get; set; } = new();

    /// <summary>The uids of players who may be shown the claims. Empty when
    ///  <see cref="Everyones"/> is true.</summary>
    public List<string> Seen { get; set; } = new();

    /// <summary>
    /// The players who may draw a new claim, and what each is allowed, by uid.
    ///
    /// Separate from <see cref="Seen"/>, because a server can show every boundary
    /// to everybody and still let only its landholders draw one.
    ///
    /// The allowance travels here rather than on a channel of its own. It is a
    /// few dozen bytes per player, it changes when their role or their claims
    /// change, and sending it here means the form always has it.
    /// </summary>
    public Dictionary<string, ClaimAllowance> Making { get; set; } = new();
}

/// <summary>
/// Builds the land claim feed the map service reads.
///
/// The world manager holds every claim, so this reads them all. It also decides
/// who may be told about them, because the service does not know what a privilege
/// is and the mod does. The markers and the player positions work the same way.
///
/// A privilege can only be tested against a player the server knows, and not
/// every web map reader is standing in the world. So the question is asked about
/// everybody the server has player data for rather than about everybody online.
/// <see cref="Permissions.Holds(ICoreServerAPI, string, string)"/> answers it.
/// </summary>
public static class ClaimFeed
{
    /// <summary>Serializes <see cref="Sorted"/> to the JSON the service reads.</summary>
    public static string Json(ICoreServerAPI api) => JsonConvert.SerializeObject(Sorted(api));

    /// <summary>
    /// Returns the claim with this key, or null when none has it.
    ///
    /// Walks the claim list rather than holding an index. A server has tens of
    /// claims and this runs only when somebody presses something in a browser, so
    /// an index would be a second copy of where the land is.
    ///
    /// Recomputes the key with <see cref="Key"/>, the same function that wrote
    /// it, so the two cannot disagree about what a claim is called.
    /// </summary>
    public static LandClaim? ByKey(ICoreServerAPI api, string key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return null;
        }

        foreach (var claim in api.World.Claims?.All ?? new List<LandClaim>())
        {
            if (claim?.Areas is null)
            {
                continue;
            }
            if (Key(claim, Rectangles(claim)) == key)
            {
                return claim;
            }
        }
        return null;
    }

    /// <summary>Returns the rectangles one claim covers, seen from above.</summary>
    private static List<LiveArea> Rectangles(LandClaim claim) =>
        claim.Areas
            .Where(area => area is not null)
            .Select(area => new LiveArea
            {
                X1 = area.MinX,
                Z1 = area.MinZ,
                X2 = area.MaxX,
                Z2 = area.MaxZ,
                Y1 = area.MinY,
                Y2 = area.MaxY,
            })
            .ToList();

    /// <summary>Returns how many claims the server holds, for `witchlight status`.</summary>
    public static int Count(ICoreServerAPI api) => api.World.Claims?.All?.Count ?? 0;

    /// <summary>
    /// Returns true when a claim belongs to a player rather than to the world.
    ///
    /// Tests the owner uid, not the owner name. Worldgen writes a name such as
    /// "Trader" on the perimeters it rules around trader camps and story
    /// structures, but writes no uid. A name is a string any mod can also choose,
    /// while an owner uid is a player the server knows.
    ///
    /// Both the drawing side and the allowance side read this one test. A claim
    /// nobody owns counts against nobody's allowance.
    /// </summary>
    private static bool Owned(LandClaim claim) => !string.IsNullOrEmpty(claim.OwnedByPlayerUid);

    /// <summary>
    /// Returns how many claims the map draws, for `witchlight status`.
    ///
    /// Reported beside <see cref="Count"/> so an operator can see why the two
    /// differ. A map drawing fewer claims than the server holds means
    /// <see cref="Settings.ClaimsWorldgen"/> is off, not that a claim is missing.
    /// </summary>
    public static int Drawn(ICoreServerAPI api)
    {
        if (Settings.ClaimsWorldgen)
        {
            return Count(api);
        }

        var drawn = 0;
        foreach (var claim in api.World.Claims?.All ?? new List<LandClaim>())
        {
            if (claim is not null && Owned(claim))
            {
                drawn++;
            }
        }
        return drawn;
    }

    /// <summary>
    /// Builds the feed: every claim, plus the two lists of who may do what with
    /// them.
    ///
    /// When any player may see the claims, sets <see cref="LiveClaims.Everyones"/>
    /// and leaves <see cref="LiveClaims.Seen"/> empty, and the service then sends
    /// them to anybody. Otherwise tests each player the server knows in turn.
    /// </summary>
    public static LiveClaims Sorted(ICoreServerAPI api)
    {
        var sorted = new LiveClaims
        {
            Claims = All(api),
            Height = api.WorldManager.MapSizeY,
            // Open to everybody is the one answer that can be given without a
            // person to give it about, so it is the one thing worth asking first.
            Everyones = Permissions.For(Permissions.ClaimsView) == Privilege.chat,
        };

        // A world with claiming switched off has nobody who may draw a claim,
        // whatever any role says. Read once, because it is a fact about the
        // world.
        var claiming = api.World.Config.GetBool("allowLandClaiming", true);

        // Group the claims by owner once. Asking per person would walk every
        // claim on the server for every player it has ever had, on the game
        // thread, every share interval.
        var byOwner = new Dictionary<string, List<LandClaim>>(StringComparer.Ordinal);
        foreach (var claim in api.World.Claims?.All ?? new List<LandClaim>())
        {
            if (claim is null || !Owned(claim))
            {
                continue;
            }

            var owner = claim.OwnedByPlayerUid;
            if (!byOwner.TryGetValue(owner, out var theirs))
            {
                theirs = byOwner[owner] = new List<LandClaim>();
            }
            theirs.Add(claim);
        }

        foreach (var uid in Known(api))
        {
            if (!sorted.Everyones && Permissions.Holds(api, uid, Permissions.ClaimsView))
            {
                sorted.Seen.Add(uid);
            }
            if (claiming
                && Permissions.Holds(api, uid, Permissions.ClaimsCreate)
                && Allowed(api, uid, byOwner) is { } allowance)
            {
                sorted.Making[uid] = allowance;
            }
        }

        return sorted;
    }

    /// <summary>
    /// Returns what one person may claim, or null when the server has no record
    /// of them.
    ///
    /// Defers to <see cref="Allowance.For"/>, which <see cref="Claiming"/> also
    /// refuses against, so the form shows the number the mod enforces.
    /// </summary>
    private static ClaimAllowance? Allowed(
        ICoreServerAPI api, string uid, Dictionary<string, List<LandClaim>> byOwner)
    {
        var mine = byOwner.TryGetValue(uid, out var theirs) ? theirs : new List<LandClaim>();
        return Allowance.For(api, uid, mine);
    }

    /// <summary>
    /// Returns the uid of everybody this server could be asked about.
    ///
    /// Reads the stored player data rather than the online players. Somebody
    /// reading the map may have signed in from a browser without joining the
    /// game, and testing a privilege against only online players would take the
    /// map away from them the moment they logged out.
    ///
    /// Folds in the online players as well, because the server writes player data
    /// for a first join at a moment of its own choosing.
    /// </summary>
    private static IEnumerable<string> Known(ICoreServerAPI api)
    {
        var uids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var uid in api.PlayerData?.PlayerDataByUid?.Keys ?? Enumerable.Empty<string>())
        {
            uids.Add(uid);
        }
        foreach (var player in api.World.AllOnlinePlayers)
        {
            if (!string.IsNullOrEmpty(player?.PlayerUID))
            {
                uids.Add(player!.PlayerUID);
            }
        }
        return uids;
    }

    /// <summary>
    /// Returns the players one claim lets in, one row each.
    ///
    /// The game keeps the access flags and the last known names in separate
    /// dictionaries keyed on uid. The form needs one row per player. A player
    /// reads and types the name, while the uid survives a rename and is what the
    /// claim stores.
    /// </summary>
    private static List<LiveGuest> Guests(LandClaim claim)
    {
        var guests = new List<LiveGuest>();
        foreach (var (uid, may) in claim.PermittedPlayerUids ?? new Dictionary<string, EnumBlockAccessFlags>())
        {
            if (string.IsNullOrEmpty(uid))
            {
                continue;
            }
            claim.PermittedPlayerLastKnownPlayerName.TryGetValue(uid, out var name);
            guests.Add(new LiveGuest
            {
                Uid = uid,
                Name = string.IsNullOrEmpty(name) ? uid : name,
                Builds = (may & EnumBlockAccessFlags.BuildOrBreak) != 0,
            });
        }
        return guests;
    }

    /// <summary>
    /// Returns a name for a claim, hashed from its owner and every corner.
    ///
    /// The game gives a claim nothing to be known by. This gives the same name
    /// for as long as nobody moves the claim, which lets a page name the claim it
    /// is looking at, and a different name once the ground changes, so a page
    /// holding a stale key is refused rather than editing land it was not
    /// looking at.
    ///
    /// Hashes with <see cref="Fnv1a"/>, which spreads well enough that two claims
    /// on one server will not collide.
    /// </summary>
    private static string Key(LandClaim claim, List<LiveArea> areas)
    {
        var parts = new List<string> { claim.OwnedByPlayerUid ?? "" };
        parts.AddRange(areas.Select(area =>
            $"|{area.X1},{area.Y1},{area.Z1},{area.X2},{area.Y2},{area.Z2}"));
        return Fnv1a.Of(parts.ToArray());
    }

    /// <summary>
    /// Returns every claim the map is willing to draw.
    ///
    /// Includes every claim a player owns. Includes the perimeters the world
    /// rules around trader camps and story structures only when
    /// <see cref="Settings.ClaimsWorldgen"/> is on. Filters them out here rather
    /// than in the page, because a claim that reached a browser can be read out
    /// of it.
    /// </summary>
    public static List<LiveClaim> All(ICoreServerAPI api)
    {
        var worldgen = Settings.ClaimsWorldgen;
        var drawn = new List<LiveClaim>();
        foreach (var claim in api.World.Claims?.All ?? new List<LandClaim>())
        {
            if (claim?.Areas is null)
            {
                continue;
            }

            if (!worldgen && !Owned(claim))
            {
                continue;
            }

            var areas = Rectangles(claim);
            if (areas.Count == 0)
            {
                continue;
            }

            drawn.Add(new LiveClaim
            {
                Key = Key(claim, areas),
                Owner = claim.LastKnownOwnerName ?? "",
                OwnerUid = claim.OwnedByPlayerUid ?? "",
                Description = claim.Description ?? "",
                Areas = areas,
                Guests = Guests(claim),
                EveryoneUses = claim.AllowUseEveryone,
                EveryoneWalks = claim.AllowTraverseEveryone,
                Volume = claim.SizeXYZ,
            });
        }

        return drawn;
    }

}

