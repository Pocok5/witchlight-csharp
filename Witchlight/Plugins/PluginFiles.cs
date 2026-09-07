using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Vintagestory.API.Common;

namespace Witchlight;

/// <summary>
/// Copies a plugin's own files out of its mod and into the map's directory.
///
/// A plugin is one thing to install. Somebody drops a mod in their Mods folder,
/// and this copies what the map serves out of the mod the first time the plugin
/// registers and again whenever the mod is newer than what was copied. Nobody
/// unpacks a second archive, and nobody needs to know the map's directory exists.
///
/// <see cref="BundledService"/> uses the same shape for the map service, for the
/// same two reasons. A mod loads from a folder while it is developed and from an
/// archive once it is installed, and both have to work. Comparing timestamps
/// keeps an unchanged plugin from being unpacked on every start.
///
/// A plugin keeps these files in a `plugin/` directory inside its mod, and that
/// directory's contents become the plugin's directory beside the map. This copies
/// the plugin's `viewer.js`, whatever it keeps in `scripts/`, and whatever it
/// ships in `assets/`. It copies nothing else, since a mod's DLL and metadata are
/// not the map's business.
///
/// The directory is named for the id the plugin registered with, not for its
/// modid. The service keys its store, its routes and its sharing by that id, so a
/// directory named anything else would be a directory nothing reads. A plugin may
/// register its modid as its id and then have one name to keep in step.
/// </summary>
internal static class PluginFiles
{
    /// <summary>The directory inside a plugin's mod that holds what the map serves.</summary>
    private const string From = "plugin";

    /// <summary>
    /// Copies one plugin's files out of its mod and into the map's directory.
    /// Returns what went wrong, or null when nothing did.
    ///
    /// A plugin whose files cannot be copied still registers and still keeps rows.
    /// It draws nothing, which belongs in the log rather than stopping a server
    /// from starting.
    /// </summary>
    internal static string? Install(Mod mod, string id, string exports, ILogger log)
    {
        var source = mod.SourcePath;
        if (string.IsNullOrEmpty(source))
        {
            return "the mod has no source path to copy from";
        }

        var into = Path.Combine(exports, "plugins", id);

        try
        {
            return Directory.Exists(source)
                ? FromFolder(Path.Combine(source, From), into, log, id)
                : FromArchive(source, into, log, id);
        }
        catch (Exception error)
        {
            return error.Message;
        }
    }

    /// <summary>
    /// Copies the files of a mod loaded from a folder, which is how it is
    /// developed.
    ///
    /// Copies file by file and only where the source is newer, so a plugin being
    /// worked on updates on the next server restart without rewriting everything
    /// it ships.
    /// </summary>
    private static string? FromFolder(string from, string into, ILogger log, string id)
    {
        if (!Directory.Exists(from))
        {
            return $"no {From} directory in the mod";
        }

        var copied = 0;
        foreach (var path in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(from, path);
            if (!Wanted(relative))
            {
                continue;
            }

            var to = Path.Combine(into, relative);
            var was = File.GetLastWriteTimeUtc(path);
            if (File.Exists(to) && File.GetLastWriteTimeUtc(to) == was)
            {
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(path, to, overwrite: true);
            File.SetLastWriteTimeUtc(to, was);
            copied++;
        }

        Said(log, id, into, copied);
        return null;
    }

    /// <summary>
    /// Unpacks the files of a mod loaded from an archive, which is how it is
    /// installed.
    ///
    /// The archive's own timestamp decides whether anything is unpacked at all.
    /// One file's date settles whether this is the same mod as last start, and
    /// saves unpacking a plugin's whole asset directory every time.
    /// </summary>
    private static string? FromArchive(string archive, string into, ILogger log, string id)
    {
        var packed = File.GetLastWriteTimeUtc(archive);
        var stamp = Path.Combine(into, ".packed");
        if (File.Exists(stamp) && File.GetLastWriteTimeUtc(stamp) == packed)
        {
            return null;
        }

        using var zip = ZipFile.OpenRead(archive);
        var prefix = From + "/";
        var copied = 0;

        foreach (var entry in zip.Entries)
        {
            // Skip directory entries, which have no name of their own, and
            // anything outside the plugin's directory in the archive.
            if (entry.Name.Length == 0 || !entry.FullName.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            var relative = entry.FullName[prefix.Length..];
            if (!Wanted(relative))
            {
                continue;
            }

            var to = Path.Combine(into, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            entry.ExtractToFile(to, overwrite: true);
            copied++;
        }

        Directory.CreateDirectory(into);
        File.WriteAllText(stamp, archive);
        File.SetLastWriteTimeUtc(stamp, packed);

        Said(log, id, into, copied);
        return null;
    }

    /// <summary>
    /// Returns true when one file inside a plugin's mod is one the map serves.
    ///
    /// Names the files it accepts rather than accepting everything under the
    /// directory. What this copies lands where the map serves from, so a rule
    /// that took whatever it found would serve whatever a mod happened to carry.
    ///
    /// Rejects any path that climbs. A zip entry is a string, and a string may be
    /// `../../map.sqlite`.
    /// </summary>
    private static bool Wanted(string relative)
    {
        var parts = relative.Split('/', '\\');
        if (parts.Any(part => part is ".." or "." || part.Length == 0))
        {
            return false;
        }

        return parts is ["viewer.js"]
            || (parts.Length > 1 && parts[0] is "scripts" or "assets");
    }

    private static void Said(ILogger log, string id, string into, int copied)
    {
        if (copied > 0)
        {
            log.Notification("[witchlight] plugin {0}: {1} file(s) copied to {2}", id, copied, into);
        }
    }
}
