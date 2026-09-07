using Vintagestory.API.Common;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace Witchlight;

/// <summary>
/// Holds one block's map appearance: the colour its top face averages to, and
/// the colour maps the game would tint it with.
/// </summary>
public class PaletteEntry
{
    /// <summary>
    /// The block id in this world. Ids are assigned per world and shift with the
    /// mod set, so the palette is keyed by code. The exported columns carry ids,
    /// so the mapping must travel with them.
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// The average colour, or null for a block this palette cannot draw. The
    /// entry is recorded either way, so the renderer can tell a colourless block
    /// from a block the palette has never heard of.
    /// </summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Include)]
    public string? Rgb { get; set; }

    /// <summary>
    /// Says which kind of colourless this is. True means the block draws nothing
    /// at all, such as air or an invisible helper. False means it draws something
    /// the builder could not work out a colour for.
    ///
    /// This is null on an entry that has a colour, and on every entry of a
    /// palette written before the field existed. A reader must be able to tell a
    /// palette that says nothing about a block from one that says it draws.
    ///
    /// Collapsing the two states made the renderer paint both the near-black it
    /// paints unexplored ground, so a block whose colour was missed, such as bare
    /// soil or forest floor, read as a hole in the world wherever somebody had
    /// been digging.
    /// </summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public bool? Invisible { get; set; }

    /// <summary>The name of the climate colour map, where the block is climate tinted.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public string? ClimateMap { get; set; }

    /// <summary>The name of the season colour map, where the block is seasonally tinted.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public string? SeasonMap { get; set; }
}

/// <summary>
/// Records what every block on the map looks like, along with what the palette
/// was built for and how to read and write it.
///
/// <see cref="PaletteBuilder"/> builds one. <see cref="PaletteTable"/> puts one
/// on the wire.
/// </summary>
public class Palette
{
    public int Version { get; set; } = 1;

    /// <summary>The game version the palette was built against. Assets change between versions.</summary>
    public string GameVersion { get; set; } = "";

    /// <summary>
    /// Identifies what this palette was built for, covering the block registry
    /// and mod set. A palette whose fingerprint no longer matches the server is
    /// stale.
    /// </summary>
    public string Fingerprint { get; set; } = "";

    /// <summary>Says where the palette came from, `server` or `client`. This decides whether to ask for a better one.</summary>
    public string Source { get; set; } = "server";

    /// <summary>
    /// Says whether an admin's own assets have settled these colours.
    ///
    /// A palette claims what the world looks like, and two players can make
    /// different claims honestly. A texture pack changes every colour on the map
    /// without changing a block code, so a palette built from one is correct for
    /// the person who sent it and wrong for the server. The colours this mod
    /// ships and the ones an ordinary player fills a gap with are good enough to
    /// draw with, and neither is a decision anybody made.
    ///
    /// The map takes an admin's palette as the one it should look like. False
    /// here makes the server ask the next admin to join, once, whatever the
    /// coverage says. See <see cref="PaletteExchange"/>.
    /// </summary>
    public bool FromAdmin { get; set; }

    /// <summary>
    /// The server's own mod set when this was written. It stays out of the shared
    /// fingerprint because a client could never match it. A change here means the
    /// textures may have moved under the same block ids.
    /// </summary>
    public string ModStamp { get; set; } = "";

    /// <summary>Counts the blocks that had textures to read when this was built.</summary>
    public int Textured { get; set; }

    /// <summary>Maps block code to appearance. The key is the code, because ids are per world.</summary>
    public Dictionary<string, PaletteEntry> Blocks { get; set; } = new();

    /// <summary>Counts the blocks that came out with a colour.</summary>
    public int Coloured => Blocks.Values.Count(entry => entry.Rgb is not null);

    /// <summary>
    /// Lists the blocks this palette should be able to draw and cannot. They have
    /// something to show and no colour was worked out for them.
    ///
    /// The list is sorted, because it is compared between one palette and the
    /// next to decide whether asking again would achieve anything, and two
    /// dictionary orderings do not compare.
    ///
    /// A palette written before <see cref="PaletteEntry.Invisible"/> existed says
    /// nothing about which of its colourless blocks draw, so it reports none.
    /// That reading cannot invent work for a server whose palette is fine.
    /// </summary>
    public IReadOnlyList<string> Uncoloured => Blocks
        .Where(pair => pair.Value.Rgb is null && pair.Value.Invisible == false)
        .Select(pair => pair.Key)
        .OrderBy(code => code, StringComparer.Ordinal)
        .ToList();

    /// <summary>
    /// Returns how much of the block registry came out with a colour. A dedicated
    /// server ships almost no block textures and so scores near zero however many
    /// blocks it lists, which signals that a client must supply a palette.
    ///
    /// The measure covers every block rather than only those with a textures
    /// entry, because a shape file can give a colour to a block that has neither.
    /// </summary>
    public double Coverage => Blocks.Count == 0 ? 0 : (double)Coloured / Blocks.Count;

    /// <summary>Returns a palette with the preferred one's gaps filled from the filler. Neither input is modified.</summary>
    public static Palette Merge(Palette preferred, Palette filler)
    {
        var merged = new Palette
        {
            Version = preferred.Version,
            GameVersion = preferred.GameVersion,
            Fingerprint = preferred.Fingerprint,
            ModStamp = preferred.ModStamp.Length > 0 ? preferred.ModStamp : filler.ModStamp,
            Source = preferred.Source,
            // Either side having come from an admin is enough. The flag records
            // whether an admin has ever settled these colours, and a gap filled
            // from elsewhere afterwards does not unsettle them.
            FromAdmin = preferred.FromAdmin || filler.FromAdmin,
            Textured = Math.Max(preferred.Textured, filler.Textured),
            Blocks = new Dictionary<string, PaletteEntry>(preferred.Blocks),
        };

        foreach (var (code, entry) in filler.Blocks)
        {
            if (!merged.Blocks.TryGetValue(code, out var existing) || existing.Rgb is null)
            {
                merged.Blocks[code] = entry;
            }
        }

        return merged;
    }

    /// <summary>Returns the palette's path inside the export directory.</summary>
    public static string PathIn(string exports) => Path.Combine(exports, "palette.json");

    /// <summary>Reads a palette back. Returns null where there is no usable one.</summary>
    public static Palette? Read(string path, ILogger? log = null)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonConvert.DeserializeObject<Palette>(File.ReadAllText(path));
        }
        catch (Exception error)
        {
            // Log this, because the caller cannot tell an unreadable palette
            // from a missing one and the two have opposite consequences. One is
            // a fresh map, the other throws a map's colours away. A palette that
            // will not parse is the only way a good one is lost without the
            // block registry having moved.
            log?.Warning(
                "[witchlight] {0} exists but could not be read ({1}) — treating it as no palette "
                + "at all, so any colours in it are lost",
                path,
                error.Message);
            return null;
        }
    }

    /// <summary>
    /// Writes the palette unless it already matches what is on disk. Returns
    /// whether anything was written.
    ///
    /// The comparison saves a redraw rather than a megabyte. The map service
    /// watches this file, and a palette arriving means every colour may have
    /// moved, so the service drops every tile and redraws the stored zoom levels.
    /// That costs seconds of a blank map, and two admins joining a server paid it
    /// twice over for two identical palettes.
    /// </summary>
    public bool Write(string path) =>
        Disk.Write(path, JsonConvert.SerializeObject(this, Formatting.Indented));
}
