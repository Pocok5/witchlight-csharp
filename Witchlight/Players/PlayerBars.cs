using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;

namespace Witchlight;

/// <summary>
/// Reads the extra bars a player's card carries beside their health and food.
///
/// A mod that gives players a resource such as mana, stamina or a level keeps it
/// on the player's entity, in the same watched attributes the game keeps health
/// and hunger in. Those are server-side and already in front of this mod, so
/// showing one costs a lookup rather than a dependency. Nothing here references
/// any mod, compiles against one, or breaks when one is uninstalled.
///
/// The operator names which attributes to read, because guessing would get it
/// wrong. Each entry names the attribute holding the value, the one holding its
/// maximum, and what colour to draw it. See `[bars]` in `witchlight.conf`, which
/// ships with what a stock Rustbound Magic uses.
///
/// **A bar is drawn only for a player who has one.** An attribute that is absent,
/// or whose maximum is zero, means the bar does not apply to that player. That
/// covers somebody who has not taken up magic, a server without the mod, and an
/// entry naming something nothing on this server keeps, and it is what makes
/// naming an attribute cost nothing.
/// </summary>
public sealed record PlayerBar(string Name, string Value, string Max, string Colour, string Group)
{
    /// <summary>The most bars one card may carry.</summary>
    private const int MostBars = 6;

    /// <summary>The character that separates the four parts of one entry.</summary>
    private const char Between = '|';

    /// <summary>
    /// The bars the settings ask for, read once and cached.
    ///
    /// Cached because every live post reads it for every player, twice a second on
    /// a busy server, and the answer changes only when somebody edits the file.
    /// </summary>
    private static IReadOnlyList<PlayerBar>? _wanted;

    /// <summary>
    /// Returns the bars this server has been asked to show.
    /// </summary>
    /// <param name="api">
    /// Used only to name the mod behind an attribute, and only for an entry that
    /// did not name one itself. A caller that passes null gets the bars with no
    /// groups.
    /// </param>
    public static IReadOnlyList<PlayerBar> Settled(ICoreAPI? api) => _wanted ??= Read(api);

    /// <summary>Returns the cached bars, for a caller that has already read them.</summary>
    public static IReadOnlyList<PlayerBar> Wanted => _wanted ??= Read(null);

    /// <summary>Clears the cache, so the next read picks up an edited settings file.</summary>
    public static void Forget() => _wanted = null;

    /// <summary>
    /// The bars a settings file with no `[bars]` table means.
    ///
    /// The same two the map service writes into a fresh file, so a file written
    /// before the table existed behaves as one written today. A table nobody has
    /// written means the defaults, and a table somebody has emptied means none.
    /// `[commands]` follows the same rule.
    ///
    /// Kept in step with `Config::default` in the service by hand, as the command
    /// privileges are. The service owns the format, and the mod has to work before
    /// the file has been written.
    /// </summary>
    private static readonly string[] ByDefault =
    {
        "Mana | entitybehavior-resource-currentmana_rm "
        + "| entitybehavior-resource-totalmaxmana_rm | #7c5cff | Rustbound Magic",
        "Magic | entitybehavior-resource-currentexptonextmaxmanalevel_rm "
        + "| entitybehavior-resource-maxexptonextmaxmanalevel_rm | #d8a24a | Rustbound Magic",
    };

    private static IReadOnlyList<PlayerBar> Read(ICoreAPI? api)
    {
        var found = new List<PlayerBar>();
        var written = Settings.HasTable("bars")
            ? Settings.Table("bars")
            : ByDefault.Select(said => ("", said));

        foreach (var (_, said) in written)
        {
            var parts = said.Split(Between);
            if (parts.Length < 3 || found.Count >= MostBars)
            {
                continue;
            }

            var name = parts[0].Trim();
            var value = parts[1].Trim();
            var max = parts[2].Trim();
            var colour = parts.Length > 3 ? parts[3].Trim() : "";
            var group = parts.Length > 4 ? parts[4].Trim() : "";

            if (name.Length > 0 && value.Length > 0 && max.Length > 0)
            {
                found.Add(new PlayerBar(
                    name, value, max, colour,
                    group.Length > 0 ? group : WhoseAttribute(api, value)));
            }
        }

        return found;
    }

    /// <summary>
    /// Returns the mod an attribute belongs to when its name says so, and null
    /// otherwise.
    ///
    /// Called only where the settings did not name a group. The game keeps a tree
    /// of names and numbers with no record of what wrote each one, so the only
    /// available signal is a mod naming its attributes after itself.
    /// `xskills:level` finds xskills, while Rustbound Magic's
    /// `entitybehavior-resource-currentmana_rm` finds nothing, which is why its
    /// entries name their group outright.
    ///
    /// Returns null rather than guessing. The viewer shows a bar with no group
    /// under a heading for the ones nobody could place.
    /// </summary>
    private static string WhoseAttribute(ICoreAPI? api, string attribute)
    {
        var named = attribute.ToLowerInvariant();
        Mod? best = null;
        foreach (var mod in api?.ModLoader?.Mods ?? Array.Empty<Mod>())
        {
            var id = mod?.Info?.ModID;
            if (string.IsNullOrEmpty(id) || id!.Length < 4 || !named.Contains(id))
            {
                continue;
            }

            // Take the longest match, so a mod called `magic` does not answer for
            // one called `magicextended`.
            if (id.Length > (best?.Info?.ModID?.Length ?? 0))
            {
                best = mod;
            }
        }

        return best?.Info?.Name ?? best?.Info?.ModID ?? "";
    }

    /// <summary>
    /// Returns this bar's value for one player, or null when their entity does not
    /// carry it.
    /// </summary>
    public LiveBar? Of(Entity? entity) => Of(entity?.WatchedAttributes);

    /// <summary>
    /// Returns this bar's value from an attribute tree alone.
    ///
    /// Split out so a test can check it. Everything the reading decides comes from
    /// the attributes, and a test cannot build an entity, so this is checkable
    /// without a world, a server or the mod itself.
    /// </summary>
    public LiveBar? Of(ITreeAttribute? watched)
    {
        if (watched is null || Number(watched, Max) is not { } most || most <= 0)
        {
            return null;
        }

        return new LiveBar
        {
            Name = Name,
            Group = Group,
            Colour = Colour,
            Value = Math.Clamp(Number(watched, Value) ?? 0f, 0f, most),
            Max = most,
        };
    }

    /// <summary>
    /// Returns a number from a player's attributes, whatever numeric type it is
    /// stored as. Returns null when the attribute is not a number.
    ///
    /// The game's readers return the default for an attribute of the wrong type
    /// rather than converting, and a mod may keep mana as an int and experience as
    /// a float, as the one this ships settings for does. So this reads the type off
    /// the attribute. Returning null rather than zero is what distinguishes a bar
    /// that does not apply from one that is empty.
    /// </summary>
    private static float? Number(ITreeAttribute watched, string key) =>
        watched.TryGetAttribute(key, out var held) ? AsNumber(held) : null;

    private static float? AsNumber(IAttribute held) => held switch
    {
        IntAttribute number => number.value,
        FloatAttribute number => number.value,
        DoubleAttribute number => (float)number.value,
        LongAttribute number => number.value,
        StringAttribute number when float.TryParse(
            number.value, NumberStyles.Float, CultureInfo.InvariantCulture, out var said) => said,
        _ => (float?)null,
    };
}
