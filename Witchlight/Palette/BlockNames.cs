using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace Witchlight;

/// <summary>
/// Exports the name the game gives each block.
///
/// The map knows a block by its code, such as `game:crop-spelt-1`, because that
/// is what a palette is keyed on and what a rendered column reads back as. A
/// person clicking the map to mark something wants the name they would see in
/// their own hands, which is "Growing spelt".
///
/// Names resolve the way the game resolves them, through the same wildcard
/// lookup that turns `block-crop-spelt-*` into one name for every growth stage.
/// Nothing here invents a name. A block the language files say nothing about is
/// left out, and the reader falls back to the code.
///
/// This goes to a file rather than over the wire because it runs to the better
/// part of a megabyte and changes only when the mod set does. It is written only
/// when it differs, so a server restarted on unchanged assets touches nothing.
/// </summary>
public static class BlockNames
{
    public static string PathIn(string exports) => Path.Combine(exports, "blocknames.json");

    /// <summary>
    /// Writes the name of every block that has one.
    ///
    /// Returns how many were named, how many of those the palette can look up,
    /// and whether the file was rewritten. The middle number matters most. This
    /// table is only ever read with a code out of the palette, so a table keyed
    /// any other way answers nothing and reports no error. One shipped keyed on
    /// short codes against a palette keyed on full ones.
    /// </summary>
    public static (int Named, int Known, int Blocks, bool Written) Export(
        ICoreAPI api,
        string exports,
        Palette palette)
    {
        var names = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var blocks = 0;

        foreach (var block in api.World.Blocks)
        {
            if (block?.Code is null)
            {
                continue;
            }
            blocks++;

            // Key on the same spelling the palette uses, domain and all, as in
            // `game:rock-granite`. A short code here would make the table
            // unlookupable and neither side would report it.
            var code = block.Code.ToString();
            if (names.ContainsKey(code))
            {
                continue;
            }

            var said = NameOf(block);
            // A name that repeats the code tells the reader nothing new, and
            // there are thousands of them.
            if (string.IsNullOrEmpty(said) || said == code)
            {
                continue;
            }

            names[code] = said;
        }

        var known = 0;
        foreach (var code in names.Keys)
        {
            if (palette.Blocks.ContainsKey(code))
            {
                known++;
            }
        }

        var written = Disk.Write(PathIn(exports), JsonConvert.SerializeObject(names));
        return (names.Count, known, blocks, written);
    }

    /// <summary>
    /// Returns one block's name, or null where the language files have none.
    ///
    /// The lookup key matches the one the game builds for a held block, so a
    /// mod's own block resolves out of that mod's language file without anything
    /// here knowing the mod exists.
    /// </summary>
    private static string? NameOf(Block block)
    {
        var code = block.Code;
        if (code is null)
        {
            return null;
        }

        var key = code.Domain + AssetLocation.LocationSeparator + "block-" + code.Path;
        return Lang.GetMatchingIfExists(key)?.Trim();
    }
}
