using System;
using System.Collections.Generic;
using ProtoBuf;

namespace Witchlight;

/// <summary>Asks a client for marker pictures. The server sends this on join
/// carrying what it already has, so the client answers with only what is
/// missing, and with nothing once the set is complete.</summary>
[ProtoContract]
public class IconRequest
{
    /// <summary>Lists the icons the server already has, so a client sends only what is new.</summary>
    [ProtoMember(1)]
    public List<string> Have { get; set; } = new();
}

/// <summary>
/// Carries marker pictures on the wire, in slices.
///
/// A dedicated server ships no SVG files. Its `textures` directory exists and
/// holds none, so the pictures a marker is drawn with can only come from a
/// machine that has the game's art. This takes the same shape as the palette for
/// the same reason.
///
/// The slicing goes by measured size rather than by count. An oversized packet
/// disconnects the player sending it, and this end cannot assume how many icons a
/// mod set adds.
/// </summary>
[ProtoContract]
public class IconTable
{
    [ProtoMember(1)] public int Part { get; set; }
    [ProtoMember(2)] public int Parts { get; set; }

    /// <summary>The icon names, in step with <see cref="Svgs"/>.</summary>
    [ProtoMember(3)] public List<string> Names { get; set; } = new();

    /// <summary>The icon files, in step with <see cref="Names"/>.</summary>
    [ProtoMember(4)] public List<byte[]> Svgs { get; set; } = new();

    /// <summary>
    /// Sets how much one packet may carry. This sits far below what a server will
    /// accept and is measured in bytes rather than icons, because an icon is a
    /// file of unknown size and a mod may ship a large one.
    /// </summary>
    public const int SliceBytes = 400 * 1024;

    /// <summary>Sets the largest one icon may be. Anything beyond this is not a map marker.</summary>
    public const int LargestIcon = 256 * 1024;

    /// <summary>Splits a set of icons into packets a server will accept.</summary>
    public static List<IconTable> Slice(IReadOnlyList<(string Name, byte[] Svg)> icons)
    {
        var slices = new List<IconTable>();
        var current = new IconTable();
        var carrying = 0;

        foreach (var (name, svg) in icons)
        {
            if (svg is null || svg.Length == 0 || svg.Length > LargestIcon)
            {
                continue;
            }

            if (current.Names.Count > 0 && carrying + svg.Length > SliceBytes)
            {
                slices.Add(current);
                current = new IconTable();
                carrying = 0;
            }

            current.Names.Add(name);
            current.Svgs.Add(svg);
            carrying += svg.Length + name.Length;
        }

        if (current.Names.Count > 0)
        {
            slices.Add(current);
        }

        for (var at = 0; at < slices.Count; at++)
        {
            slices[at].Part = at;
            slices[at].Parts = slices.Count;
        }

        return slices;
    }

    /// <summary>Collects every name and file across a full set of slices, ready to write.</summary>
    public static List<(string Name, byte[] Svg)> Assemble(IEnumerable<IconTable> slices)
    {
        var all = new List<(string, byte[])>();
        foreach (var slice in slices)
        {
            var count = Math.Min(slice.Names.Count, slice.Svgs.Count);
            for (var at = 0; at < count; at++)
            {
                var name = Icons.NameOf(slice.Names[at]);
                if (name is not null)
                {
                    all.Add((name, slice.Svgs[at]));
                }
            }
        }
        return all;
    }
}
