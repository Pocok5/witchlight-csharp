using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Vintagestory.API.Common;

namespace Witchlight;

/// <summary>
/// Copies out the images that tint a block for where and when it is.
///
/// Grass, leaves and water ship as greyscale masks and take their colour from
/// one of these, looked up by temperature and rainfall. The renderer needs the
/// pictures themselves, and mods can add their own, so the files are copied
/// beside the palette rather than named in it.
/// </summary>
public static class ColourMaps
{
    /// <summary>Returns the directory the colour maps are copied into.</summary>
    public static string DirectoryIn(string exports) => Path.Combine(exports, "colormaps");

    /// <summary>
    /// Copies every colour map this side can see.
    ///
    /// Returns how many were written and how many were found. A restart on
    /// unchanged assets writes none, and "0 written" alone reads like a server
    /// that found no colour maps at all, which draws a forest grey.
    /// </summary>
    public static (int Written, int Found) Export(ICoreAPI api, string exports)
    {
        var directory = DirectoryIn(exports);
        Directory.CreateDirectory(directory);
        var written = 0;
        var found = 0;
        var padding = new Dictionary<string, int>();

        foreach (var asset in api.Assets.GetMany("config/colormaps.json"))
        {
            List<ColorMap>? maps;
            try
            {
                maps = asset.ToObject<List<ColorMap>>();
            }
            catch (Exception)
            {
                continue;
            }

            foreach (var map in maps ?? new List<ColorMap>())
            {
                if (map?.Code is null || map.Texture?.Base is null)
                {
                    continue;
                }

                var path = map.Texture.Base.Clone()
                    .WithPathPrefixOnce("textures/")
                    .WithPathAppendixOnce(".png");
                if (api.Assets.TryGet(path) is not { } texture)
                {
                    continue;
                }

                found++;
                padding[map.Code] = map.Padding;

                // Write only where the bytes differ. The map service watches
                // this directory, and a colour map arriving means every tinted
                // block may have changed.
                if (Disk.WriteBytes(Path.Combine(directory, map.Code + ".png"), texture.Data))
                {
                    written++;
                }
            }
        }

        // Record how far into each picture the usable part begins. The climate
        // maps are a 256 square inside a 264 one, and reading the border as the
        // map puts every lookup a few pixels out. The error is worst at the
        // extremes, where the hottest and coldest ground is. The number comes
        // from the asset and cannot be read off the picture, so it travels
        // beside it.
        Disk.Write(
            Path.Combine(directory, "padding.json"), JsonConvert.SerializeObject(padding));

        return (written, found);
    }
}
