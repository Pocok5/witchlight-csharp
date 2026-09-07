using System;
using System.Collections.Generic;
using System.Linq;
using SkiaSharp;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace Witchlight;

/// <summary>
/// Turns a texture into the one colour that stands for it.
///
/// Nothing here knows what a block is. It finds the PNG behind an asset
/// reference and averages it. The palette asks this of block textures, of
/// overlay textures, and of the textures a shape file names.
/// </summary>
public static class TextureColours
{
    /// <summary>
    /// Accumulates a running average of pixels, weighted by alpha.
    ///
    /// The weighting keeps the transparent parts of a leaf or a grass tuft from
    /// washing the colour out. A fern is mostly nothing, and counting that
    /// nothing as black gives every fern the colour of a shadow.
    /// </summary>
    public struct Average
    {
        private double _red, _green, _blue, _weight;
        private long _pixels;

        /// <summary>Reports whether anything was opaque enough to count.</summary>
        public readonly bool Any => _weight > 0;

        /// <summary>
        /// Returns how much of the square this covers, from 0 to 1.
        ///
        /// A colour on its own cannot say whether the texture it came from is
        /// what somebody looking down at the block would see. A branch texture
        /// and a leaf texture both belong to a branchy leaves block, and only one
        /// of them stands for the block.
        /// </summary>
        public readonly double Covers => _pixels == 0 ? 0 : _weight / _pixels;

        /// <summary>Adds every pixel of one texture file to the average.</summary>
        public void Add(IAsset asset)
        {
            using var bitmap = SKBitmap.Decode(asset.Data);
            if (bitmap is null)
            {
                return;
            }

            _pixels += (long)bitmap.Width * bitmap.Height;

            for (var y = 0; y < bitmap.Height; y++)
            {
                for (var x = 0; x < bitmap.Width; x++)
                {
                    var pixel = bitmap.GetPixel(x, y);
                    if (pixel.Alpha == 0)
                    {
                        continue;
                    }

                    var alpha = pixel.Alpha / 255.0;
                    _red += pixel.Red * alpha;
                    _green += pixel.Green * alpha;
                    _blue += pixel.Blue * alpha;
                    _weight += alpha;
                }
            }
        }

        /// <summary>Adds every texture file behind one reference to the average.</summary>
        public void AddAll(ICoreAPI api, AssetLocation texture)
        {
            foreach (var asset in Resolve(api, texture))
            {
                Add(asset);
            }
        }

        /// <summary>Returns the average as a CSS hex colour, or null where nothing was counted.</summary>
        public readonly string? Hex => _weight <= 0
            ? null
            : $"#{Round(_red):x2}{Round(_green):x2}{Round(_blue):x2}";

        private readonly int Round(double channel) => (int)Math.Round(channel / _weight);
    }

    /// <summary>
    /// Carries what one texture comes out as: its colour, and how much of the
    /// square it covers. A caller choosing between a block's textures needs both.
    /// See <see cref="Average.Covers"/>.
    /// </summary>
    public readonly record struct Paint(string? Hex, double Covers)
    {
        /// <summary>The result where nothing was opaque enough to count.</summary>
        public static readonly Paint None = new(null, 0);
    }

    /// <summary>
    /// Returns the average colour of one of a block's textures, with its
    /// overlays.
    ///
    /// Block variants share textures, so each texture is decoded once and the
    /// answer cached for the rest of the build.
    /// </summary>
    public static Paint Of(ICoreAPI api, CompositeTexture texture)
    {
        var overlays = Overlays(texture);
        var key = texture.Base + "|" + string.Join(",", overlays.Select(overlay => overlay.ToString()));
        if (Cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var decoded = Decode(api, texture, overlays);
        Cache[key] = decoded;
        return decoded;
    }

    /// <summary>Returns the cached answer for a key, computing it once where there is none.</summary>
    public static Paint Once(string key, Func<Paint> work)
    {
        if (Cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var decoded = work();
        Cache[key] = decoded;
        return decoded;
    }

    /// <summary>Clears the cache so the next build starts fresh.</summary>
    public static void Forget() => Cache.Clear();

    private static readonly Dictionary<string, Paint> Cache = new();

    private static Paint Decode(ICoreAPI api, CompositeTexture texture, List<AssetLocation> overlays)
    {
        // Grass-covered soil is a dirt texture with a grass overlay on top, and
        // the overlay is the part a map shows. Averaging the base alone turns
        // every meadow into mud.
        var average = new Average();
        foreach (var overlay in overlays)
        {
            average.AddAll(api, overlay);
        }
        if (average.Any)
        {
            return new Paint(average.Hex, average.Covers);
        }

        average.AddAll(api, texture.Base);
        return average.Any ? new Paint(average.Hex, average.Covers) : Paint.None;
    }

    private static List<AssetLocation> Overlays(CompositeTexture texture)
    {
        return (texture.BlendedOverlays ?? Array.Empty<BlendedOverlayTexture>())
            .Select(overlay => overlay?.Base)
            .Where(location => location is not null)
            .Select(location => location!)
            .ToList();
    }

    /// <summary>
    /// Reports whether a texture reference is the game's stand-in for a texture
    /// it was never given, rather than a texture in its own right.
    ///
    /// `unknown.png` is the missing-texture checker, white and opaque over the
    /// whole square. Averaged as a colour it beats any real texture that covers
    /// less than the full square, so every block wearing it came out `#fff9f9`,
    /// that file's own average. Ruins, clutter, banners, pies and fire all drew
    /// as white patches on correctly coloured ground.
    ///
    /// A block left with no texture after this check has nothing to draw, which
    /// the palette already records.
    /// </summary>
    public static bool Placeholder(string? path) =>
        path is not null
        && (path.Equals("unknown", StringComparison.Ordinal)
            || path.EndsWith("/unknown", StringComparison.Ordinal));

    /// <inheritdoc cref="Placeholder(string?)"/>
    public static bool Placeholder(AssetLocation? texture) => Placeholder(texture?.Path);

    /// <summary>
    /// Returns the texture files behind one texture reference.
    ///
    /// A block's texture may be a wildcard such as `coral/shelf/blue*`, which the
    /// client expands at bake time into the set of alternates it picks from per
    /// position. There is no atlas here to bake against, so this expands the
    /// wildcard the same way and every match contributes to the average.
    /// </summary>
    public static IEnumerable<IAsset> Resolve(ICoreAPI api, AssetLocation texture)
    {
        // Return nothing rather than the checker. Every route to a colour
        // arrives here, so refusing it once refuses it everywhere.
        if (Placeholder(texture))
        {
            return Array.Empty<IAsset>();
        }

        return Matching(api, texture.Clone().WithPathPrefixOnce("textures/"), ".png")
            .Select(location => api.Assets.TryGet(location))
            .Where(asset => asset is not null)
            .Select(asset => asset!);
    }

    /// <summary>
    /// Returns every asset one reference names, expanding a wildcard where there
    /// is one.
    ///
    /// The results are ordered, so a block whose texture varies gets the same
    /// average on every machine and every run rather than whatever the filesystem
    /// listed first.
    /// </summary>
    public static IEnumerable<AssetLocation> Matching(
        ICoreAPI api,
        AssetLocation path,
        string extension)
    {
        if (!path.Path.Contains('*'))
        {
            return new[] { path.WithPathAppendixOnce(extension) };
        }

        return api.Assets
            .GetLocations(path.Path[..path.Path.IndexOf('*')], path.Domain)
            .Where(location => location.Path.EndsWith(extension, StringComparison.Ordinal))
            .OrderBy(location => location.Path, StringComparer.Ordinal);
    }
}
