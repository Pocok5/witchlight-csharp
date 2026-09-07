using System;
using System.Collections.Generic;
using System.Linq;
using ProtoBuf;

namespace Witchlight;

/// <summary>
/// Asks one player's client for a palette, when the server cannot build a usable
/// one itself.
/// </summary>
[ProtoContract]
public class PaletteRequest
{
    /// <summary>What the server needs a palette for. The client echoes it back.</summary>
    [ProtoMember(1)]
    public string Fingerprint { get; set; } = "";
}

/// <summary>
/// Carries a palette on the wire, in slices.
///
/// Three things keep it small. Block codes are not sent at all, because the
/// server can turn an id back into a code from its own registry, and the codes
/// were the largest part of the message. Colour map names repeat across thousands
/// of blocks, so they are interned and referenced by index. The numbers are
/// packed rather than tagged one by one.
///
/// It still travels in slices, because a server rejects an oversized packet by
/// disconnecting the client. The size of a mod set must not decide whether this
/// works.
/// </summary>
[ProtoContract]
public class PaletteTable
{
    [ProtoMember(1)] public string Fingerprint { get; set; } = "";
    [ProtoMember(2)] public string GameVersion { get; set; } = "";
    [ProtoMember(3)] public int Textured { get; set; }

    /// <summary>The colour map names, referenced by index below.</summary>
    [ProtoMember(4)] public List<string> ColorMaps { get; set; } = new();

    /// <summary>Which slice this is, and how many there are in total.</summary>
    [ProtoMember(5)] public int Part { get; set; }
    [ProtoMember(6)] public int Parts { get; set; }

    [ProtoMember(7, IsPacked = true)] public List<int> Ids { get; set; } = new();

    // All three are zigzagged. They are mostly -1, and a plain varint spends ten
    // bytes on a negative number.
    /// <summary>The colour as packed 0xRRGGBB, or -1 for a block with nothing to draw.</summary>
    [ProtoMember(8, IsPacked = true, DataFormat = DataFormat.ZigZag)]
    public List<int> Colors { get; set; } = new();

    /// <summary>An index into <see cref="ColorMaps"/>, or -1 for none.</summary>
    [ProtoMember(9, IsPacked = true, DataFormat = DataFormat.ZigZag)]
    public List<int> Climate { get; set; } = new();

    [ProtoMember(10, IsPacked = true, DataFormat = DataFormat.ZigZag)]
    public List<int> Season { get; set; } = new();

    /// <summary>
    /// Says which kind of colourless each block is. 1 means it draws nothing at
    /// all, 0 means it draws something this client could not colour, and -1 means
    /// the sender said nothing.
    ///
    /// This travels because the first two are different facts and the server
    /// cannot work out either for itself, since its own assets are the ones that
    /// could not answer. Without it, every colourless block in a client's palette
    /// arrived saying nothing. The server could not tell a gap worth asking about
    /// from a block with nothing to show, so it stopped noticing gaps, and the map
    /// painted air and the invisible placeholders of large structures as
    /// unexplored ground.
    ///
    /// The third state makes an older client's palette safe to take. A missing
    /// list reads as saying nothing rather than as a wall of zeroes, which would
    /// have turned every one of those blocks into a gap the server chased
    /// forever.
    /// </summary>
    [ProtoMember(11, IsPacked = true, DataFormat = DataFormat.ZigZag)]
    public List<int> Hidden { get; set; } = new();

    /// <summary>Sets the blocks per slice. Each costs about ten bytes.</summary>
    public const int SliceSize = 8000;

    /// <summary>Splits a palette into packets a server will accept.</summary>
    public static List<PaletteTable> Slice(Palette palette)
    {
        var maps = new List<string>();
        var seen = new Dictionary<string, int>();
        int Intern(string? name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return -1;
            }
            if (!seen.TryGetValue(name, out var at))
            {
                at = maps.Count;
                maps.Add(name);
                seen[name] = at;
            }
            return at;
        }

        var entries = palette.Blocks.Values.ToList();
        var parts = (entries.Count + SliceSize - 1) / SliceSize;
        var slices = new List<PaletteTable>();

        for (var part = 0; part < parts; part++)
        {
            var slice = new PaletteTable
            {
                Fingerprint = palette.Fingerprint,
                GameVersion = palette.GameVersion,
                Textured = palette.Textured,
                Part = part,
                Parts = parts,
            };

            foreach (var entry in entries.Skip(part * SliceSize).Take(SliceSize))
            {
                slice.Ids.Add(entry.Id);
                slice.Colors.Add(Pack(entry.Rgb));
                slice.Climate.Add(Intern(entry.ClimateMap));
                slice.Season.Add(Intern(entry.SeasonMap));
                slice.Hidden.Add(entry.Invisible is null ? -1 : entry.Invisible.Value ? 1 : 0);
            }

            slices.Add(slice);
        }

        // Interning fills while the slices are built, so assign the finished
        // list to every slice rather than leaving each with a prefix.
        foreach (var slice in slices)
        {
            slice.ColorMaps = maps;
        }

        return slices;
    }

    /// <summary>
    /// Rebuilds a palette from every slice, using the server's own registry to
    /// turn ids back into the codes the palette is keyed by.
    /// </summary>
    public static Palette Assemble(IEnumerable<PaletteTable> slices, Func<int, string?> codeOf)
    {
        var ordered = slices.OrderBy(slice => slice.Part).ToList();
        var first = ordered.FirstOrDefault() ?? new PaletteTable();

        var palette = new Palette
        {
            GameVersion = first.GameVersion,
            Fingerprint = first.Fingerprint,
            Source = "client",
            Textured = first.Textured,
        };

        foreach (var slice in ordered)
        {
            for (var i = 0; i < slice.Ids.Count; i++)
            {
                var id = slice.Ids[i];
                var code = codeOf(id);
                if (code is null)
                {
                    continue;
                }

                palette.Blocks[code] = new PaletteEntry
                {
                    Id = id,
                    Rgb = Unpack(slice.Colors.ElementAtOrDefault(i)),
                    Invisible = slice.DrawingAt(i),
                    ClimateMap = slice.NameAt(slice.Climate.ElementAtOrDefault(i)),
                    SeasonMap = slice.NameAt(slice.Season.ElementAtOrDefault(i)),
                };
            }
        }

        return palette;
    }

    private string? NameAt(int index)
    {
        return index >= 0 && index < ColorMaps.Count ? ColorMaps[index] : null;
    }

    /// <summary>
    /// Returns what this slice says about whether the block at
    /// <paramref name="at"/> draws, or null where it says nothing.
    ///
    /// This checks the whole list against the ids rather than reading one entry.
    /// A slice from a client older than this field carries no list at all, and
    /// <c>ElementAtOrDefault</c> would answer 0, meaning it draws something, for
    /// every block in it.
    /// </summary>
    private bool? DrawingAt(int at)
    {
        if (Hidden.Count != Ids.Count)
        {
            return null;
        }
        return Hidden[at] < 0 ? null : Hidden[at] == 1;
    }

    private static int Pack(string? rgb)
    {
        if (string.IsNullOrEmpty(rgb))
        {
            return -1;
        }
        return int.TryParse(rgb.TrimStart('#'), System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture, out var value)
            ? value
            : -1;
    }

    private static string? Unpack(int packed)
    {
        return packed < 0 ? null : $"#{packed & 0xffffff:x6}";
    }
}
