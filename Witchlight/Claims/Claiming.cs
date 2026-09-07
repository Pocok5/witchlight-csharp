using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace Witchlight;

/// <summary>
/// One land claim somebody drew on the web map.
///
/// Carries two corners and a description. The service squares the rectangle up
/// and takes the owner from the session. <see cref="Claiming"/> decides
/// everything else about whether the claim may exist.
/// </summary>
public class WantedClaim
{
    /// <summary>The uid of the player who asked, taken from their session.</summary>
    public string Uid { get; set; } = "";

    public string Description { get; set; } = "";

    /// <summary>The horizontal corners, west and north first.</summary>
    public int X1 { get; set; }
    public int Z1 { get; set; }
    public int X2 { get; set; }
    public int Z2 { get; set; }

    /// <summary>
    /// The lowest and highest block the claim reaches, lower first.
    ///
    /// The form asks for these rather than assuming the full height of the world.
    /// Depth accounts for most of a claim's volume and a role's allowance is
    /// measured in volume, so a full-height claim would give a survival player a
    /// square thirty-two blocks across with no way to trade depth for width.
    /// </summary>
    public int Y1 { get; set; }
    public int Y2 { get; set; }
}

/// <summary>The players and permissions a claim is being asked to let in.</summary>
public class WantedGuests
{
    /// <summary>The players named, by the name the game knows them as. The mod
    ///  turns these into uids, because the mod is the half that can.</summary>
    public List<string> Names { get; set; } = new();

    /// <summary>The game's two permissions that apply to everybody.</summary>
    public bool EveryoneUses { get; set; }
    public bool EveryoneWalks { get; set; }
}

/// <summary>
/// A change to a claim that already exists: what it is called, and who it lets
/// in.
///
/// Does not change the ground. Moving a boundary has to be judged against every
/// other claim and against an allowance, and the map cannot show a player what
/// they would give up. A player who wants different ground draws a new claim.
/// </summary>
public class EditedClaim
{
    /// <summary>The claim to change, by the key <see cref="ClaimFeed"/> gave it.</summary>
    public string Key { get; set; } = "";

    /// <summary>The uid of the player who asked, taken from their session.</summary>
    public string Uid { get; set; } = "";

    public string Description { get; set; } = "";

    public WantedGuests Allowed { get; set; } = new();
}

/// <summary>One claim a player asked to give up.</summary>
public class UnwantedClaim
{
    public string Key { get; set; } = "";
    public string Uid { get; set; } = "";
}

/// <summary>The claim requests the service was holding, in one reply.</summary>
public class AskedClaims
{
    public List<WantedClaim> Make { get; set; } = new();
    public List<EditedClaim> Change { get; set; } = new();
    public List<UnwantedClaim> Remove { get; set; } = new();
}

/// <summary>How many requests of each kind the mod applied.</summary>
public readonly record struct Claimed(int Made, int Changed, int Removed)
{
    public bool Anything => Made > 0 || Changed > 0 || Removed > 0;
}

/// <summary>
/// Makes, changes and releases the land claims players ask for on the web map.
///
/// Applies every rule the game applies to `/land claim`, and applies them only
/// here. A web map that could take land the game would refuse is a way around the
/// server's rules, and a copy of those rules in the service would be a second
/// opinion on a question with one right answer.
///
/// Reads the game's own numbers: the world config, the privilege, the role's
/// allowance and minimum size, how many claims the player already has, and every
/// claim already on the map. Refuses in the same cases and for the same reasons.
/// The rectangle and the depth both come from the form, since the player taking
/// the land chooses how deep it goes.
///
/// Tests the map's `[claims] create` permission as well, never instead. It only
/// narrows who may take land through the map.
/// </summary>
public static class Claiming
{
    /// <summary>
    /// Applies every request in the service's reply that may be applied, and
    /// returns how many of each kind landed.
    ///
    /// Logs each refusal once, naming who and why. Sends nothing back to the
    /// player, who is in a browser watching the page for the claim to appear.
    /// </summary>
    public static Claimed Apply(ICoreServerAPI api, AskedClaims? asked)
    {
        if (asked is null)
        {
            return default;
        }

        var made = 0;
        foreach (var wanted in asked.Make)
        {
            if (wanted is null || string.IsNullOrEmpty(wanted.Uid))
            {
                api.Logger.Warning("[witchlight] the map asked for a land claim with no owner");
                continue;
            }

            if (Make(api, wanted) is { } refusal)
            {
                Refused(api, wanted.Uid, "claim that land", refusal);
                continue;
            }
            made++;
        }

        var changed = 0;
        foreach (var edit in asked.Change)
        {
            if (edit is null || string.IsNullOrEmpty(edit.Uid) || string.IsNullOrEmpty(edit.Key))
            {
                continue;
            }

            if (Change(api, edit) is { } refusal)
            {
                Refused(api, edit.Uid, "change that claim", refusal);
                continue;
            }
            changed++;
        }

        var removed = 0;
        foreach (var gone in asked.Remove)
        {
            if (gone is null || string.IsNullOrEmpty(gone.Uid) || string.IsNullOrEmpty(gone.Key))
            {
                continue;
            }

            if (Give(api, gone) is { } refusal)
            {
                Refused(api, gone.Uid, "give up that claim", refusal);
                continue;
            }
            removed++;
        }

        return new Claimed(made, changed, removed);
    }

    /// <summary>
    /// Logs why one player was refused, so an operator can see who was turned
    /// away and why.
    /// </summary>
    private static void Refused(ICoreServerAPI api, string uid, string doing, string why) =>
        api.Logger.Notification("[witchlight] {0} may not {1}: {2}", uid, doing, why);

    /// <summary>
    /// Changes what a claim is called and who it lets in. Returns why not, or
    /// null when the change was made.
    ///
    /// Removes the claim and adds it back rather than editing it in place. The
    /// game indexes claims by region and tells every client on `Remove` and
    /// `Add`, so editing a live claim would change what the server holds and tell
    /// nobody. The game's own `/land claim save` works the same way.
    /// </summary>
    private static string? Change(ICoreServerAPI api, EditedClaim edit)
    {
        if (Held(api, edit.Key) is not { } claim)
        {
            return "there is no claim by that name any more — the map may be out of date";
        }
        if (!Owns(api, claim, edit.Uid))
        {
            return "it is not theirs";
        }

        var changed = claim.Clone();
        changed.Description = edit.Description ?? "";
        changed.AllowUseEveryone = edit.Allowed?.EveryoneUses ?? false;
        changed.AllowTraverseEveryone = edit.Allowed?.EveryoneWalks ?? false;
        Invite(api, changed, edit.Allowed?.Names ?? new List<string>());

        api.World.Claims.Remove(claim);
        api.World.Claims.Add(changed);
        return null;
    }

    /// <summary>Releases a claim. Returns why not, or null when it was released.</summary>
    private static string? Give(ICoreServerAPI api, UnwantedClaim gone)
    {
        if (Held(api, gone.Key) is not { } claim)
        {
            return "there is no claim by that name any more — the map may be out of date";
        }
        if (!Owns(api, claim, gone.Uid))
        {
            return "it is not theirs";
        }

        return api.World.Claims.Remove(claim) ? null : "the game would not release it";
    }

    /// <summary>
    /// Returns the claim the map named, or null when no claim has that key.
    ///
    /// A key is derived from the claim's ground, so a key that matches nothing
    /// names a claim that has moved since the page was told about it. Returning
    /// null refuses the change, rather than editing whichever claim now sits
    /// where the page thinks it is looking.
    /// </summary>
    private static LandClaim? Held(ICoreServerAPI api, string key) =>
        ClaimFeed.ByKey(api, key);

    /// <summary>
    /// Returns true when this player may change or release this claim.
    ///
    /// Allows the owner, and anybody holding `commandplayer`, which is what
    /// vanilla's `/land adminfree` requires to delete somebody else's claim. The
    /// map applies the same test rather than a rule of its own.
    /// </summary>
    private static bool Owns(ICoreServerAPI api, LandClaim claim, string uid) =>
        claim.OwnedByPlayerUid == uid
        || (api.World.PlayerByUid(uid)?.HasPrivilege(Privilege.commandplayer) ?? false);

    /// <summary>
    /// Writes the named players onto a claim, replacing whoever was on it.
    ///
    /// Takes the whole guest list each time rather than a difference. The form
    /// holds the whole list and sends it, and diffing against a claim somebody
    /// else may have edited since would give a wrong answer.
    ///
    /// Drops a name the server has never seen rather than refusing the whole
    /// change, so one typo does not lose the other rows. The form shows what came
    /// back, so a name that did not take is visible on the next post.
    ///
    /// Grants use, traverse and building, which is what `/land claim grant ...
    /// all` gives. The map offers one kind of guest rather than three.
    /// </summary>
    private static void Invite(ICoreServerAPI api, LandClaim claim, List<string> names)
    {
        claim.PermittedPlayerUids.Clear();
        claim.PermittedPlayerLastKnownPlayerName.Clear();

        foreach (var name in names)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var data = api.PlayerData?.GetPlayerDataByLastKnownName(name.Trim());
            if (data is null || string.IsNullOrEmpty(data.PlayerUID))
            {
                api.Logger.Notification(
                    "[witchlight] no player called \"{0}\" on this server, so the claim does not "
                    + "name them", name.Trim());
                continue;
            }

            claim.PermittedPlayerUids[data.PlayerUID] = EnumBlockAccessFlags.BuildOrBreak
                | EnumBlockAccessFlags.Use | EnumBlockAccessFlags.Traverse;
            claim.PermittedPlayerLastKnownPlayerName[data.PlayerUID] =
                data.LastKnownPlayername ?? name.Trim();
        }
    }

    /// <summary>
    /// Makes one claim. Returns one sentence saying why it cannot be made, or
    /// null when it was made.
    ///
    /// Applies the game's refusals in the game's own order, so the map takes a
    /// claim only where `/land claim` would have taken it from the same player.
    /// </summary>
    private static string? Make(ICoreServerAPI api, WantedClaim wanted)
    {
        // The world's switch comes first. A world with claiming turned off has no
        // claims to allow or refuse.
        if (!api.World.Config.GetBool("allowLandClaiming", true))
        {
            return "land claiming is off in this world's configuration";
        }

        // The game's privilege first, then the map's, which only narrows it.
        // Refusing on the game's privilege first makes the message name the real
        // reason.
        if (!Permissions.Holds(api, wanted.Uid, Permissions.ClaimsCreate))
        {
            return $"claiming land on the map is for {Permissions.Who(Permissions.ClaimsCreate)}";
        }

        // Read the claim list once and refuse outright when there is none.
        // Reading `Claims?.All` and then adding through `Claims.Add` asks twice
        // whether this world has land claims, and the second call would put a
        // claim nowhere and report success.
        if (api.World.Claims is not { } held)
        {
            return "this world has no land claims to add to";
        }

        var everyones = held.All ?? new List<LandClaim>();
        var mine = Allowance.Owned(everyones, wanted.Uid);

        // The same numbers the web form was shown. A player who has never joined
        // has no role, and so no allowance to check.
        if (Allowance.For(api, wanted.Uid, mine) is not { } allowance)
        {
            return "this server has no record of them, so it has no allowance to check";
        }

        // Inclusive of every corner. A rectangle dragged from one block to
        // another covers both, and a claim from y=60 to y=80 includes the block
        // at 80.
        //
        // Clamp to the world rather than refusing a box that reaches past it. The
        // depths are two numbers a player typed, and "from the bottom of the
        // world" should give the bottom of the world.
        var floor = Math.Clamp(Math.Min(wanted.Y1, wanted.Y2), 0, api.WorldManager.MapSizeY);
        var ceiling = Math.Clamp(Math.Max(wanted.Y1, wanted.Y2), 0, api.WorldManager.MapSizeY);
        var ground = new Cuboidi(
            wanted.X1, floor, wanted.Z1,
            wanted.X2 + 1, Math.Min(ceiling + 1, api.WorldManager.MapSizeY), wanted.Z2 + 1);

        if (allowance.Refuses(ground) is { } refusal)
        {
            return refusal;
        }

        // Test against every claim, including the player's own. Two of a player's
        // own claims on one piece of ground is still an overlap.
        if (everyones.FirstOrDefault(claim => claim?.Intersects(ground) == true) is { } already)
        {
            var whose = string.IsNullOrEmpty(already.LastKnownOwnerName)
                ? "another claim"
                : $"a claim of {already.LastKnownOwnerName}'s";
            return $"it overlaps {whose}";
        }

        // `CreateClaim` takes the owner name and privilege level, so read them
        // here and write the uid on afterwards. Every reader of a claim shows
        // `LastKnownOwnerName`. The allowance carries size and count only.
        var data = api.PlayerData?.GetPlayerDataByUid(wanted.Uid);
        var role = data is null ? null : api.Permissions.GetRole(data.RoleCode);
        if (data is null || role is null)
        {
            return "this server has no record of them, so it has no allowance to check";
        }

        var claiming = LandClaim.CreateClaim(data.LastKnownPlayername ?? "", role.PrivilegeLevel);
        claiming.OwnedByPlayerUid = wanted.Uid;
        claiming.Description = wanted.Description ?? "";

        var error = claiming.AddArea(ground);
        if (error != EnumClaimError.NoError)
        {
            return error == EnumClaimError.Overlapping
                ? "it overlaps one of their own areas"
                : "it is not next to their other areas";
        }

        // The game's API indexes the claim by region, saves it with the world and
        // tells every client. The mod keeps no copy of where the claims are.
        held.Add(claiming);
        return null;
    }
}
