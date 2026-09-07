using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using Newtonsoft.Json;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace Witchlight;

/// <summary>
/// Supplies the base game's colours, recorded once and carried inside this
/// assembly.
///
/// A dedicated server's install ships 46 block texture files against a full
/// game's 9,587, so the palette it builds for itself colours nearly nothing.
/// Such a server drew a flat map from the moment it came up until a player
/// joined with this mod on. That wait is why <see cref="PaletteExchange"/>
/// exists, and for a server running the base game it was a wait for information
/// nobody needed to send, because the base game's blocks look the same on every
/// server.
///
/// Recording them colours the map fully before the first player arrives. The
/// asking is left for what this side cannot know: a mod's blocks, and a texture
/// pack an admin has chosen.
///
/// The recording is keyed by code and nothing else. A block id is assigned per
/// world and means nothing on another server, so the recording carries none and
/// every entry takes this world's id as it is read. A code this server has never
/// heard of is dropped, and a colour it already has is left alone. See
/// <see cref="Palette.Merge"/> for the general form of that second rule.
///
/// `bake-palette.py` in this repository refreshes the recording. It records a
/// real palette.json rather than deriving one, because working out a block's
/// colour takes the game's own assets and a second implementation here would
/// drift from <see cref="PaletteBuilder"/>.
/// </summary>
public static class Vanilla
{
    /// <summary>
    /// The recording's resource name, as the compiler builds it: the root
    /// namespace, then the path to the file with its separators turned into dots.
    /// </summary>
    private const string Resource = "Witchlight.Palette.vanilla.json.gz";

    /// <summary>Holds what was read out of the assembly, and whether reading was tried.</summary>
    private static Palette? _recorded;
    private static bool _read;

    /// <summary>
    /// Fills what this palette has no colour for from the recording. Returns how
    /// many colours it supplied.
    ///
    /// This changes the palette in place rather than returning a merged copy, so
    /// a caller cannot take the count and drop the palette.
    /// </summary>
    public static int Fill(ICoreAPI api, Palette palette)
    {
        if (Read(api.Logger) is not { } recorded)
        {
            return 0;
        }

        var filled = 0;
        foreach (var (code, entry) in recorded.Blocks)
        {
            // Fill only what this palette is missing. A colour built from this
            // server's own assets is this server's answer and is not overwritten.
            //
            // This server calling a block invisible is not honoured. That
            // judgement means no textures were there to try and the shape gave
            // nothing either, which is only trustworthy where the textures
            // existed. On a dedicated server they do not, and 1,202 blocks that
            // plainly draw, including beds, banners, bamboo and barrel cactus,
            // come out of its own build labelled as drawing nothing. Honouring
            // that label would skip exactly the blocks this recording supplies.
            // A block the game itself reports as drawing nothing is settled
            // earlier. See `PaletteBuilder` and `EnumDrawType.Empty`.
            if (palette.Blocks.TryGetValue(code, out var held) && held.Rgb is not null)
            {
                continue;
            }

            // The recording carries no ids, so a block this server does not have
            // gets no entry.
            if (api.World.GetBlock(new AssetLocation(code)) is not { } block)
            {
                continue;
            }

            palette.Blocks[code] = new PaletteEntry
            {
                Id = block.Id,
                Rgb = entry.Rgb,
                Invisible = entry.Invisible,
                ClimateMap = entry.ClimateMap,
                SeasonMap = entry.SeasonMap,
            };

            if (entry.Rgb is not null)
            {
                filled++;
            }
        }

        return filled;
    }

    /// <summary>Returns the game version the recording was made against.</summary>
    public static string RecordedFor(ILogger log) => Read(log)?.GameVersion ?? "";

    /// <summary>
    /// Logs what the recording supplied, and warns where it is worth doubting.
    ///
    /// A recording made against another game version is the one case where these
    /// colours can be wrong without anything else here noticing, because block
    /// codes outlive releases and the textures under them do not.
    /// </summary>
    public static void Report(ICoreAPI api, int filled)
    {
        if (filled == 0)
        {
            return;
        }

        var recorded = RecordedFor(api.Logger);
        var running = GameVersion.ShortGameVersion;

        api.Logger.Notification(
            "[witchlight] {0} colours came from the base game palette shipped with this mod{1}",
            filled,
            recorded == running || recorded.Length == 0
                ? ""
                : $", which was recorded against {recorded} and this is {running} — run "
                  + $"`{Commands.Short} {Permissions.Palette}` if the map's colours look wrong");
    }

    /// <summary>
    /// Reads the recording out of this assembly, once.
    ///
    /// A recording that will not read means the mod was packaged wrong. It costs
    /// a flat map on a dedicated server and nothing worse, so this logs a warning
    /// and carries on without it.
    /// </summary>
    private static Palette? Read(ILogger log)
    {
        if (_read)
        {
            return _recorded;
        }

        _read = true;
        try
        {
            using var packed = typeof(Vanilla).Assembly.GetManifestResourceStream(Resource);
            if (packed is null)
            {
                log.Warning(
                    "[witchlight] this build carries no base game palette ({0} is not in the "
                    + "assembly), so a server without block textures starts with a flat map",
                    Resource);
                return null;
            }

            using var plain = new GZipStream(packed, CompressionMode.Decompress);
            using var text = new StreamReader(plain);
            using var json = new JsonTextReader(text);
            _recorded = new JsonSerializer().Deserialize<Palette>(json);
        }
        catch (Exception error)
        {
            log.Warning(
                "[witchlight] the base game palette shipped with this mod could not be read "
                + "({0}), so a server without block textures starts with a flat map",
                error.Message);
            _recorded = null;
        }

        return _recorded;
    }
}
