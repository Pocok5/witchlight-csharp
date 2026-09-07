using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Vintagestory.API.Common;

namespace Witchlight;

/// <summary>
/// Exports the pictures a marker is drawn with.
///
/// A waypoint names its icon, such as `gravestone`, `home` or `trader`, and the
/// game draws each from an SVG under `textures/icons/worldmap`. Each is a
/// silhouette filled with a single near-black, so the game and the map can both
/// tint one any colour.
///
/// Reads every domain rather than only the game's own, so a mod that adds markers
/// adds its icons without anything knowing about it in advance.
///
/// The file names carry a sort prefix the game orders its menu by, such as
/// `0-circle` or `01-turnip`. A waypoint names the icon without that prefix, so
/// this drops it on the way out.
/// </summary>
public static class Icons
{
    /// <summary>
    /// The asset path the game keeps the icons under. Public because the server
    /// and the client both read it, and spelling it once keeps them in step.
    /// </summary>
    public const string AssetPath = "textures/icons/worldmap";

    /// <summary>
    /// Writes every icon the server can see.
    ///
    /// Reports how many were written, how many there are, and which mods they came
    /// from. A restart on unchanged assets writes none, and "0 written" alone
    /// reads like a server that found no icons at all.
    /// </summary>
    public static (int Written, int Found, string From) Export(ICoreAPI api, string exports)
    {
        var dir = DirectoryIn(exports);
        var domains = new SortedSet<string>(StringComparer.Ordinal);
        var written = 0;
        var found = 0;

        try
        {
            Directory.CreateDirectory(dir);
            foreach (var asset in api.Assets.GetMany(AssetPath, null, true))
            {
                if (asset?.Location is null || !asset.Location.Path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var name = NameOf(asset.Location.GetName());
                if (name is null)
                {
                    continue;
                }

                var data = asset.Data;
                if (data is null || data.Length == 0)
                {
                    continue;
                }

                found++;
                if (Disk.WriteBytes(Path.Combine(dir, name + ".svg"), data))
                {
                    written++;
                }
                domains.Add(asset.Location.Domain ?? "game");
            }
        }
        catch (Exception error)
        {
            api.Logger.Warning("[witchlight] could not write marker icons: {0}", error.Message);
        }

        return (written, found, domains.Count == 0 ? "nowhere" : string.Join(", ", domains));
    }

    /// <summary>
    /// Writes icons a client sent. Returns how many were new or different.
    ///
    /// Leaves an icon already on disk and unchanged alone, so a client sending
    /// the same set twice does not rewrite files the map service is watching.
    /// </summary>
    public static int Accept(IReadOnlyList<(string Name, byte[] Svg)> icons, string exports)
    {
        var dir = DirectoryIn(exports);
        var written = 0;

        try
        {
            Directory.CreateDirectory(dir);
            foreach (var (name, svg) in icons)
            {
                var safe = NameOf(name);
                if (safe is null || svg is null || svg.Length == 0)
                {
                    continue;
                }

                if (Disk.WriteBytes(Path.Combine(dir, safe + ".svg"), svg))
                {
                    written++;
                }
            }
        }
        catch (Exception)
        {
            // A picture that cannot be written draws its marker as a plain
            // shape, which is not worth failing a join over.
        }

        return written;
    }

    /// <summary>Returns the directory the icons are written to.</summary>
    public static string DirectoryIn(string exports) => Path.Combine(exports, "icons");

    /// <summary>
    /// Returns the icons already on disk, by the names a waypoint would ask for.
    ///
    /// Lives here because this file decides what an icon is called and where one
    /// is kept, so a second reader of that directory would have to agree about
    /// both.
    /// </summary>
    public static List<string> Stored(string exports)
    {
        var dir = DirectoryIn(exports);
        if (!Directory.Exists(dir))
        {
            return new List<string>();
        }

        var names = new List<string>();
        foreach (var path in Directory.GetFiles(dir, "*.svg"))
        {
            if (Path.GetFileNameWithoutExtension(path) is { Length: > 0 } name)
            {
                names.Add(name);
            }
        }
        return names;
    }

    /// <summary>
    /// Returns the name a waypoint would use for this file, or null when it
    /// cannot be one.
    ///
    /// The name becomes a file and then part of a URL, and it arrives from
    /// whatever mods are installed, so this reduces it to the characters that are
    /// safe in both.
    /// </summary>
    public static string? NameOf(string fileName)
    {
        var name = fileName.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)
            ? fileName[..^4]
            : fileName;

        // Drop the sort prefix the game orders its menu by. A waypoint omits it.
        var dash = name.IndexOf('-');
        if (dash > 0 && IsAllDigits(name[..dash]))
        {
            name = name[(dash + 1)..];
        }

        var safe = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            if (c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-')
            {
                safe.Append(c);
            }
            else if (c is >= 'A' and <= 'Z')
            {
                safe.Append(char.ToLowerInvariant(c));
            }
        }

        return safe.Length == 0 ? null : safe.ToString();
    }

    private static bool IsAllDigits(string text)
    {
        foreach (var c in text)
        {
            if (c is < '0' or > '9')
            {
                return false;
            }
        }
        return text.Length > 0;
    }
}
