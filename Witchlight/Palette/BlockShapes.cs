using System;
using System.Collections.Generic;
using Vintagestory.API.Common;

namespace Witchlight;

/// <summary>
/// Reads what a block's shape file says about how it looks.
///
/// A fern has no `textures` block. It is a shape, and the shape file names the
/// textures and carries the colour maps that tint them. Without reading it those
/// blocks have no colour and no tint, and the map shows a grey hole wherever one
/// grows.
///
/// One read answers two questions: the average colour of everything the shape
/// draws with, and the tint its elements declare. Both are cached per shape,
/// because every variant of a block shares one shape.
/// </summary>
public static class BlockShapes
{
    /// <summary>Clears the cached tints so the next build starts fresh.</summary>
    public static void Forget() => Tints.Clear();

    /// <summary>
    /// Returns the average colour of every texture this block's shape uses, and
    /// how much of the square they cover. Returns nothing where the block has no
    /// shape, or the shape names nothing that loads.
    ///
    /// The coverage travels with the colour because the caller weighs the shape
    /// against the block's own textures. A reed declares a texture covering three
    /// per cent of the block, its two seed heads, with the whole plant in the
    /// shape file. Colouring from that texture alone made every reed bed the
    /// colour of a seed head. Whichever candidate covers more of the block now
    /// stands for it.
    /// </summary>
    public static TextureColours.Paint AverageColour(ICoreAPI api, Block block)
    {
        var shape = block.Shape?.Base;
        return shape is null
            ? TextureColours.Paint.None
            : TextureColours.Once("shape:" + shape, () => Decode(api, shape));
    }

    /// <summary>
    /// Returns the tint a block's shape declares, for a block that declares none
    /// itself.
    ///
    /// This answers only for a shape already decoded, which is the order the
    /// palette builds in. A block with no textures of its own is read through its
    /// shape, and that read fills this cache.
    /// </summary>
    public static (string? Climate, string? Season) TintOf(Block block)
    {
        var shape = block.Shape?.Base?.ToString();
        return shape is not null && Tints.TryGetValue(shape, out var tint) ? tint : (null, null);
    }

    /// <summary>Holds tints found in shape files, keyed by shape path.</summary>
    private static readonly Dictionary<string, (string? Climate, string? Season)> Tints = new();

    private static TextureColours.Paint Decode(ICoreAPI api, AssetLocation shape)
    {
        var path = shape.Clone().WithPathPrefixOnce("shapes/").WithPathAppendixOnce(".json");
        var average = new TextureColours.Average();

        // Shape paths carry the same wildcards texture paths do.
        foreach (var candidate in TextureColours.Matching(api, path, ".json"))
        {
            var asset = api.Assets.TryGet(candidate);
            if (asset is null)
            {
                continue;
            }

            ShapeFile? read;
            try
            {
                read = asset.ToObject<ShapeFile>();
            }
            catch (Exception)
            {
                continue;
            }

            var tint = FirstTint(read?.Elements);
            if (tint.Climate is not null || tint.Season is not null)
            {
                Tints[shape.ToString()] = tint;
            }

            foreach (var texture in read?.Textures?.Values ?? NoTextures)
            {
                // A generic shape can still hold an unresolved variant.
                if (string.IsNullOrEmpty(texture) || texture.Contains('{'))
                {
                    continue;
                }

                // A shared shape substitutes its own textures for the block's.
                // `block/basic/cube` says `all: unknown`. See
                // `TextureColours.Placeholder`, which owns that rule for the
                // block's own textures as well as for a shape's.
                if (TextureColours.Placeholder(texture))
                {
                    continue;
                }

                average.AddAll(api, new AssetLocation(shape.Domain, texture));
            }

            // One variant is enough. Variants differ in arrangement, not colour.
            if (average.Any)
            {
                break;
            }
        }

        return average.Any
            ? new TextureColours.Paint(average.Hex, average.Covers)
            : TextureColours.Paint.None;
    }

    /// <summary>An empty collection, so the loop below has nothing to walk.</summary>
    private static readonly Dictionary<string, string>.ValueCollection NoTextures =
        new Dictionary<string, string>().Values;

    /// <summary>
    /// Holds the parts of a shape file this needs: its textures, and the colour
    /// maps its elements are tinted by.
    /// </summary>
    private class ShapeFile
    {
        public Dictionary<string, string>? Textures { get; set; }
        public List<Element>? Elements { get; set; }
    }

    private class Element
    {
        public string? ClimateColorMap { get; set; }
        public string? SeasonColorMap { get; set; }
        public List<Element>? Children { get; set; }
    }

    /// <summary>
    /// Returns the first tint declared anywhere in a shape's element tree. A fern
    /// keeps its colour maps on the child elements that make up each frond rather
    /// than on the top element.
    /// </summary>
    private static (string? Climate, string? Season) FirstTint(List<Element>? elements, int depth = 0)
    {
        if (elements is null || depth > MaxDepth)
        {
            return (null, null);
        }

        foreach (var element in elements)
        {
            if (element.ClimateColorMap is not null || element.SeasonColorMap is not null)
            {
                return (element.ClimateColorMap, element.SeasonColorMap);
            }

            var nested = FirstTint(element.Children, depth + 1);
            if (nested.Climate is not null || nested.Season is not null)
            {
                return nested;
            }
        }

        return (null, null);
    }

    /// <summary>Sets how far down a shape's elements to look before giving up.</summary>
    private const int MaxDepth = 8;
}
