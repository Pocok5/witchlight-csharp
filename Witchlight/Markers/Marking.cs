using System;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace Witchlight;

/// <summary>
/// Answers a marker asked for from in game.
///
/// The client sends a place and sometimes everything a marker is. This decides
/// what the marker becomes and whether it is made at all. None of that judgement
/// is on the client, because the server can read the block at a position and the
/// server is what the map service speaks to about what somebody has kept.
///
/// Makes the marker through <see cref="Markers"/>, as
/// <see cref="PendingMarkers"/> does for a marker asked for on the web. Both add a
/// waypoint under a guid, record who may see it, and send the marker feed again.
/// </summary>
public static class Marking
{
    /// <summary>
    /// Returns the code and display name of the block that names this marker.
    ///
    /// Read on the server rather than taken from the client. The code decides which
    /// preset applies, and a client that could name the block could name any block.
    /// </summary>
    public static (string Code, string Name) BlockAt(ICoreServerAPI api, MarkAsk ask) =>
        BlockAt(api, new BlockPos(ask.BlockX, ask.BlockY, ask.BlockZ));

    /// <summary>Returns the block at one position and its display name. Returns
    ///  nothing for air and for a chunk nobody has loaded.</summary>
    public static (string Code, string Name) BlockAt(ICoreServerAPI api, BlockPos at)
    {
        var block = api.World.BlockAccessor.GetBlock(at);
        if (block is null || block.Code is null || block.BlockId == 0)
        {
            return ("", "");
        }

        return (block.Code.ToString(), block.GetPlacedBlockName(api.World, at) ?? "");
    }

    /// <summary>
    /// Returns the block a marker at this place is about.
    ///
    /// A marker made on the web lands where somebody clicked on a map drawn from
    /// above, which shows the surface, so the marker takes the standing height and
    /// means the block under its feet. Tries the block at the position first, since
    /// a marker typed into the form can be anywhere, a cave floor included.
    /// </summary>
    public static string CodeUnder(ICoreServerAPI api, int x, int y, int z)
    {
        var here = BlockAt(api, new BlockPos(x, y, z)).Code;
        return here.Length > 0 ? here : BlockAt(api, new BlockPos(x, y - 1, z)).Code;
    }

    /// <summary>
    /// Makes the marker where there is enough to make one, and otherwise returns
    /// what a window needs to finish it.
    ///
    /// The one case that makes nothing is a press of the key over a block no preset
    /// names. That is not a failure, so the reply carries the defaults a window
    /// should open on rather than an error.
    /// </summary>
    public static MarkReply Answer(
        ICoreServerAPI api,
        Visibility visibility,
        Origins origins,
        IServerPlayer player,
        MarkAsk ask,
        Person person,
        (string Code, string Name) block)
    {
        var preset = ask.UsePreset ? Presets.For(person, block.Code) : null;
        var reply = Starting(ask, person, block, preset);

        if (ask.UsePreset && preset is null)
        {
            reply.Said = block.Code.Length == 0
                ? "Nothing there to mark. Look at a block, or stand on one."
                : $"No preset for {Named(block)}.";
            reply.Yours = true;
            // Send everything they have kept, in the order the map lists them, so
            // the window can start from a preset instead of the block's defaults.
            reply.Presets = person.Presets
                .OrderBy(kept => kept.Title, StringComparer.OrdinalIgnoreCase)
                .Select(kept => new PresetOffer
                {
                    Pattern = kept.Pattern,
                    Title = kept.Title,
                    Icon = kept.Icon,
                    Color = kept.Color,
                    Private = kept.Private is { } said ? Mark.Says(said) : Mark.Unsaid,
                })
                .ToList();
            return reply;
        }

        var waypoint = Markers.Make(
            api,
            Guid.NewGuid().ToString(),
            player.PlayerUID,
            new Vec3d(ask.X, ask.Y, ask.Z),
            reply.Title,
            reply.Icon,
            Markers.Packed(reply.Color) ?? Markers.White,
            pinned: false);

        if (waypoint is null)
        {
            reply.Said = "The server has no waypoint layer, so nothing could be marked.";
            return reply;
        }

        // Record the choice whichever way it went, as the web form's markers do.
        // The operator's setting is the fallback for a marker nobody decided about,
        // and somebody who marked something decided, including by agreeing with it.
        visibility.Choose(waypoint.Guid, reply.Private == Mark.Private);
        // Record the block this was made on while the chunk is in hand. A preset
        // made from this marker later is keyed on it, and by then the ore may be
        // mined out.
        origins.Made(waypoint.Guid, block.Code);
        reply.Made = true;
        reply.Said = $"Marked {reply.Title}"
            + (reply.Private == Mark.Private ? " — private." : " — everyone can see it.");
        return reply;
    }

    /// <summary>
    /// Returns the preset this request should be kept as, or null.
    ///
    /// Keys it on what was typed, and otherwise on the block itself. Returns null
    /// rather than failing when there is nothing to key it on, which is the rule
    /// the map's own form follows. The marker is the point and the preset is extra.
    /// </summary>
    public static Preset? Keeping(MarkAsk ask, MarkReply made, (string Code, string Name) block)
    {
        if (!ask.KeepPreset)
        {
            return null;
        }

        var pattern = ask.Pattern.Trim();
        if (pattern.Length == 0)
        {
            pattern = BlockPattern.Widened(block.Code);
        }
        if (pattern.Length == 0)
        {
            return null;
        }

        return new Preset
        {
            Pattern = pattern,
            Title = made.Title,
            Icon = made.Icon,
            Color = made.Color,
            Private = made.Private == Mark.Private,
        };
    }

    /// <summary>
    /// Returns what the marker is before anything is made of it: the preset where
    /// one applies, and what the client typed where none does.
    ///
    /// Settles every field here, so what is made and what a window opens on are one
    /// answer read twice.
    /// </summary>
    private static MarkReply Starting(
        MarkAsk ask, Person person, (string Code, string Name) block, Preset? preset)
    {
        // Their own choice wins over the operator's, which is the order the map's
        // own form reads them in.
        var byDefault = person.PrivateByDefault ?? Settings.MarkersPrivateByDefault;
        var kept = preset is null
            ? Mark.IsPrivate(ask.Private, byDefault)
            : preset.Private ?? byDefault;

        var title = preset?.Title ?? ask.Title;
        if (string.IsNullOrWhiteSpace(title))
        {
            title = Named(block);
        }

        return new MarkReply
        {
            X = ask.X,
            Y = ask.Y,
            Z = ask.Z,
            BlockX = ask.BlockX,
            BlockY = ask.BlockY,
            BlockZ = ask.BlockZ,
            Block = block.Name,
            // Widen the block's variant number into a wildcard, so one preset
            // answers for a whole family rather than for the one stage of grass
            // that happened to be underfoot. A player who wants the exact block
            // takes the star back out.
            Pattern = BlockPattern.Widened(block.Code),
            Title = Markers.Title(title),
            Icon = Markers.Picture(preset?.Icon ?? ask.Icon),
            Color = Colour(preset?.Color ?? ask.Color),
            Private = Mark.Says(kept),
            // Use what this player set on the map for themselves, which is what
            // the window would open with. A request that came from that window has
            // already been answered by whoever was looking at it.
            KeepPreset = ask.UsePreset ? person.PresetsByDefault : ask.KeepPreset,
        };
    }

    /// <summary>Returns the name for a marker nobody named: the block's name, or
    ///  the word the game gives an unnamed waypoint.</summary>
    private static string Named((string Code, string Name) block)
    {
        if (block.Name.Length > 0)
        {
            return block.Name;
        }
        return block.Code.Length > 0 ? block.Code : Markers.Unnamed;
    }

    /// <summary>Returns the colour as the map writes one, or white for anything
    ///  that is not six hex digits behind a hash.</summary>
    private static string Colour(string? color)
    {
        var said = (color ?? "").Trim().ToLowerInvariant();
        return Markers.Packed(said) is null ? Markers.WhiteHex : said;
    }
}
