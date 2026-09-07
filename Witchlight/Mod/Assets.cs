using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace Witchlight;

/// <summary>
/// Reads what the mod needs out of the game's assets, while they are still loaded.
///
/// Part of <see cref="WitchlightSystem"/>, as the commands and the greeting are.
///
/// Kept in its own file because it runs on a different clock. Everything else runs
/// once a world is up, while this runs during loading, in the window between the
/// textures being read and the server freeing them.
/// </summary>
public partial class WitchlightSystem
{

    public override void AssetsLoaded(ICoreAPI api) => Report(api, "AssetsLoaded");

    /// <summary>
    /// Reads everything that has to be read while the assets are still in memory.
    ///
    /// The server frees block textures once assets are loaded, so a palette not
    /// built here cannot be built at all. The icons, colour maps and block names
    /// come out of the same assets and are read in the same pass.
    /// </summary>
    public override void AssetsFinalize(ICoreAPI api)
    {
        Report(api, "AssetsFinalize");
        _raw = PaletteExchange.BuildFromAssets(api);
    }

    /// <summary>
    /// Writes everything the assets said, once there is a directory to write it to,
    /// and returns the settled palette.
    ///
    /// The palette is built at asset load because the textures behind it are freed
    /// straight afterwards. The colour maps, the marker pictures and the block
    /// names come out of assets the server keeps, so this reads them here.
    ///
    /// Returns the palette rather than leaving it in a field, so a caller cannot
    /// read it before this has run.
    /// </summary>
    private PaletteExchange.Built? WriteWhatTheAssetsSaid(ICoreServerAPI api)
    {
        if (_raw is not { } raw)
        {
            api.Logger.Warning(
                "[witchlight] no palette was built while the assets were loading, so the map "
                + "has no colours. This is a bug in this mod.");
            return null;
        }

        var settled = PaletteExchange.Settle(api, Settings.Exports, raw);
        var palette = settled.Palette;

        var (maps, mapsFound) = ColourMaps.Export(api, Settings.Exports);
        api.Logger.Notification("[witchlight] colour maps: {0} of {1} written", maps, mapsFound);

        var (icons, iconsFound, from) = Icons.Export(api, Settings.Exports);
        api.Logger.Notification(
            "[witchlight] marker icons: {0} of {1} written, from {2}", icons, iconsFound, from);

        var named = BlockNames.Export(api, Settings.Exports, palette);
        api.Logger.Notification(
            "[witchlight] block names: {0} of {1} blocks named, {2} of them in the palette{3}",
            named.Named,
            named.Blocks,
            named.Known,
            named.Written ? "" : " — unchanged, not rewritten");
        if (named.Named > 0 && named.Known == 0)
        {
            api.Logger.Warning(
                "[witchlight] no named block is in the palette, so the map will show codes "
                + "rather than names. The two are keyed differently, which is a bug in this mod.");
        }

        return settled;
    }

    /// <summary>
    /// Logs whether block textures are readable at this point. The answer decides
    /// whether a palette can be built server-side at all.
    /// </summary>
    private static void Report(ICoreAPI api, string stage)
    {
        var blocks = api.World.Blocks;
        api.Logger.Notification(
            "[witchlight] {0}: {1} of {2} blocks have textures in memory",
            stage,
            blocks.Count(block => block?.Textures is { Count: > 0 }),
            blocks.Count);
    }
}
