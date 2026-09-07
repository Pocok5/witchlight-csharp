using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;

namespace Witchlight;

/// <summary>
/// Carries what a build of the palette found, beside the palette itself.
///
/// The findings are returned rather than left in static fields, so two builds in
/// one process do not read each other's. A client running the command after
/// joining does two builds.
/// </summary>
public sealed class PaletteReport
{
    /// <summary>Counts the blocks that ended with no colour at all.</summary>
    public int Missing { get; init; }

    /// <summary>Counts the misses per group of block.</summary>
    public IReadOnlyDictionary<string, int> Counts { get; init; } =
        new Dictionary<string, int>();

    /// <summary>Holds a sample of what could not be resolved, for diagnosing gaps.</summary>
    public IReadOnlyList<string> Failures { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Builds the block colour palette from the assets the server already has.
///
/// Other map mods build this on a client, using the client's texture atlas. The
/// textures ship with the server too, the asset category is universal, and the
/// rule the game uses to pick a block's map colour is short enough to follow
/// here: the texture named by the <c>textureCodeForBlockColor</c> attribute,
/// else <c>up</c>, else the first one. Building server-side means no admin has
/// to run a client command.
///
/// <see cref="TextureColours"/> reads a texture and <see cref="BlockShapes"/>
/// reads a shape file. This class chooses which texture stands for a block and
/// which colour map tints it.
/// </summary>
public static class PaletteBuilder
{
    public static Palette Build(ICoreAPI api, out PaletteReport report)
    {
        TextureColours.Forget();
        BlockShapes.Forget();

        var palette = new Palette
        {
            GameVersion = GameVersion.ShortGameVersion,
            Fingerprint = Fingerprint.Of(api),
            ModStamp = Fingerprint.LocalStamp(api),
            Source = api.Side == EnumAppSide.Client ? "client" : "server",
            Textured = api.World.Blocks.Count(block => block?.Textures is { Count: > 0 }),
        };

        var missed = new Misses();

        foreach (var block in api.World.Blocks)
        {
            if (block?.Code is null)
            {
                continue;
            }

            // Ask the game what it draws before looking for a colour, rather
            // than inferring invisibility from having found none.
            //
            // Air is the block this matters for. It has no textures, so nothing
            // below finds one, and it carries the default cube shape it never
            // draws, so the shape fallback averaged that cube and gave air a
            // near-white colour. The exporter writes air for a column whose
            // chunk holds no terrain, which is half a freshly seeded map, so
            // half a new map painted near-white instead of reading as ground
            // nobody has been to.
            if (block.DrawType == EnumDrawType.Empty)
            {
                palette.Blocks[block.Code.ToString()] =
                    new PaletteEntry { Id = block.Id, Rgb = null, Invisible = true };
                missed.Note(block.Code.ToString(), $"{block.Code}: the game draws nothing for it");
                continue;
            }

            // Try every texture that could stand for the block, best first, until
            // one draws something. Taking only the best and giving up left bare
            // soil black: the grass overlay a soil block wears is the preferred
            // texture, and on the grassless variant that overlay is a fully
            // transparent file.
            var painted = TextureColours.Paint.None;
            var tried = 0;
            foreach (var texture in MapColourTextures(api, block))
            {
                tried++;
                painted = TextureColours.Of(api, texture);
                if (painted.Hex is not null)
                {
                    break;
                }
            }

            // Plants and other shape-based blocks keep their textures in the
            // shape file they point at rather than declaring any of their own.
            //
            // The shape is weighed against what the block declares rather than
            // asked only when nothing else answered. A reed declares two
            // textures, both a seed head covering three per cent of the square,
            // with the whole plant in its shape. The seed head answered first and
            // every reed bed came out the colour of a dead stalk, dark enough to
            // read as unexplored ground. Whatever covers most of the block stands
            // for it.
            var shaped = BlockShapes.AverageColour(api, block);
            if (shaped.Hex is not null && shaped.Covers > painted.Covers)
            {
                painted = shaped;
            }

            var rgb = painted.Hex;

            if (rgb is null)
            {
                // Record the block, and record which kind of colourless it is. A
                // block with nothing to try has nothing to draw, such as air or
                // an invisible helper, and the map is right to leave it bare. A
                // block whose textures were there and drew nothing is a gap in
                // this palette rather than a hole in the world. Recording that
                // lets the map paint it as ground and the server go and find a
                // colour for it.
                missed.Note(block.Code.ToString(), tried == 0
                    ? $"{block.Code}: no texture, and shape {block.Shape?.Base} gave none either"
                    : $"{block.Code}: none of its {tried} texture(s) drew anything, "
                      + $"and shape {block.Shape?.Base} gave none either");
                palette.Blocks[block.Code.ToString()] =
                    new PaletteEntry { Id = block.Id, Rgb = null, Invisible = tried == 0 };
                continue;
            }

            palette.Blocks[block.Code.ToString()] = new PaletteEntry
            {
                Id = block.Id,
                Rgb = rgb,
                ClimateMap = ClimateTintOf(block),
                SeasonMap = SeasonTintOf(block),
            };
        }

        report = missed.Report();
        return palette;
    }

    /// <summary>
    /// Returns the colour map that tints this block on a map.
    ///
    /// The map-specific properties are the right answer where they are set. Some
    /// block classes populate them only in OnCollectTextures, which never runs on
    /// a server. BlockLeaves is the one that matters, and without the fallback
    /// every forest renders grey. The plain fields come from the block JSON and
    /// are populated on both sides.
    /// </summary>
    private static string? ClimateTintOf(Block block) =>
        block.ClimateColorMapForMap ?? block.ClimateColorMap ?? BlockShapes.TintOf(block).Climate;

    private static string? SeasonTintOf(Block block) =>
        block.SeasonColorMapForMap ?? block.SeasonColorMap ?? BlockShapes.TintOf(block).Season;

    /// <summary>
    /// Yields every texture that could stand for this block's map colour, best
    /// first, in the game's own order of preference. This mirrors
    /// Block.LoadTextureSubIdForBlockColor.
    ///
    /// The order is what the block says to use, then the coverage layer, then the
    /// top face, then whatever else it has. A coverage layer is what sits on top,
    /// such as grass over soil or snow over rock. Without it every meadow on the
    /// map is the colour of the dirt underneath.
    ///
    /// This yields all of them rather than only the best, because preferring a
    /// texture does not mean that texture has a colour in it. A grassless soil
    /// block still wears the coverage layer, and on that variant the layer is a
    /// file with every pixel transparent. The block the map most often draws
    /// after somebody digs was the one block the palette had no colour for, so
    /// the map went black exactly where the world had changed.
    /// </summary>
    private static IEnumerable<CompositeTexture> MapColourTextures(ICoreAPI api, Block block)
    {
        if (block.Textures is not { Count: > 0 })
        {
            yield break;
        }

        var named = block.Attributes?["textureCodeForBlockColor"].AsString(null);
        var given = new HashSet<string>(StringComparer.Ordinal);

        // Three codes state intent, in the order they state it: what the block
        // author named, the overlay a block wears on top, and the face a map
        // looks down on.
        foreach (var code in new[] { named, "specialSecondTexture", "up" })
        {
            if (code is not null
                && block.Textures.TryGetValue(code, out var texture)
                && texture?.Base is not null
                && !TextureColours.Placeholder(texture.Base)
                && given.Add(code))
            {
                yield return texture;
            }
        }

        // Then whatever else it has, the texture covering most of the block
        // first. A block that draws in some colour somewhere is better mapped in
        // that colour than not at all.
        //
        // The order is by coverage rather than the order the block lists its
        // textures, which is where the game takes its own answer. A block
        // reaching this far has said nothing about which way is up, so its
        // dictionary order answers nothing. Branchy leaves carry a `branch`
        // texture and two of leaves. `branch` sorts first alphabetically and is
        // the one part nobody looking down at a tree can see, so every branchy
        // tree came out the colour of the twig hidden under it: a dark brown,
        // multiplied by the leaf tint, within a shade of the colour this map
        // paints unexplored ground.
        foreach (var pair in block.Textures
            // Skip the missing-texture checker rather than letting it average to
            // nothing, so a block whose every texture is the stand-in counts as
            // having none to try. That tells the exporter to draw whatever the
            // block stands on rather than a hole where a bookshelf is.
            .Where(pair => pair.Value?.Base is not null
                && !TextureColours.Placeholder(pair.Value.Base)
                && !given.Contains(pair.Key))
            .OrderByDescending(pair => TextureColours.Of(api, pair.Value).Covers)
            // Order ties by name, so a block whose textures cover equal ground
            // is coloured the same on every machine.
            .ThenBy(pair => pair.Key, StringComparer.Ordinal)
            .ToList())
        {
            given.Add(pair.Key);
            yield return pair.Value;
        }
    }

    /// <summary>
    /// Collects why blocks got no colour during one build.
    ///
    /// Invisible helper blocks make up the bulk of the misses, so they are
    /// counted rather than listed.
    /// </summary>
    private sealed class Misses
    {
        private const int MostListed = 25;

        private readonly Dictionary<string, int> _counts = new();
        private readonly List<string> _failures = new();
        private int _missing;

        public void Note(string code, string message)
        {
            _missing++;

            var group = code.Contains("multiblock") || code.EndsWith(":air")
                ? Helpers
                : code.Split('-')[0];
            _counts.TryGetValue(group, out var count);
            _counts[group] = count + 1;

            if (group != Helpers && _failures.Count < MostListed)
            {
                _failures.Add(message);
            }
        }

        public PaletteReport Report() =>
            new() { Missing = _missing, Counts = _counts, Failures = _failures };

        private const string Helpers = "(helper blocks)";
    }
}
