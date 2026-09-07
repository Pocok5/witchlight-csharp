using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.API.Common;

namespace Witchlight;

/// <summary>
/// Works out what one person may claim, from their role and the claims they hold.
///
/// The single owner of the numbers `/land claim add` enforces. The web form
/// displays what this returns and the mod refuses against what this returns, so a
/// player is shown the number they are held to.
/// </summary>
public static class Allowance
{
    /// <summary>
    /// Returns what this person may claim, or null when the server has no record
    /// of them. A player who has never joined has no role and so no allowance.
    /// </summary>
    /// <param name="api">The server API, for the player data and the role.</param>
    /// <param name="uid">The player to report on.</param>
    /// <param name="mine">The claims that player already owns.</param>
    public static ClaimAllowance? For(ICoreServerAPI api, string uid, List<LandClaim> mine)
    {
        var data = api.PlayerData?.GetPlayerDataByUid(uid);
        var role = data is null ? null : api.Permissions.GetRole(data.RoleCode);
        if (data is null || role is null)
        {
            return null;
        }

        var least = role.LandClaimMinSize ?? new Vec3i(1, 1, 1);
        return new ClaimAllowance
        {
            Allowance = (long)role.LandClaimAllowance + data.ExtraLandClaimAllowance,
            Used = mine.Sum(claim => (long)claim.SizeXYZ),
            MaxAreas = role.LandClaimMaxAreas + data.ExtraLandClaimAreas,
            Areas = mine.Count,
            LeastX = least.X,
            LeastY = least.Y,
            LeastZ = least.Z,
        };
    }

    /// <summary>Returns the claims one uid owns, out of every claim given.</summary>
    public static List<LandClaim> Owned(IEnumerable<LandClaim?> every, string uid)
    {
        return every.Where(claim => claim?.OwnedByPlayerUid == uid).ToList()!;
    }
}
