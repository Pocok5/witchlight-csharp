using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Vintagestory.API.Common;

namespace Witchlight;

/// <summary>
/// Getting a plugin's own files out of its mod and beside the map.
///
/// A plugin is one thing to install. Somebody drops a mod in their Mods folder
/// and that is the whole of it — they do not also unpack a second archive into
/// the map's directory, and they do not have to know that directory exists.
/// What the map serves is copied out of the mod itself, here, the first time it
/// registers and again whenever the mod is newer than what was copied.
///
/// The same shape <see cref="BundledService"/> already uses to get the map
/// service out of Witchlight's own mod, and for the same two reasons: a mod
/// loaded from a folder is how it is developed and one loaded from an archive is
/// how it is installed, and both have to work; and comparing timestamps is what
/// keeps an unchanged plugin from being unpacked on every start.
///
/// A plugin keeps them in a `plugin/` directory inside its mod, and the contents
/// of that directory become the plugin's own directory beside the map. What is
/// copied out of it is the plugin's `viewer.js`, whatever it keeps in `scripts/`,
/// and whatever it ships in `assets/`. Nothing else — a mod carries its own DLL
/// and its own metadata, and neither of those is the map's business.
///
/// The directory it lands in is named for the id the plugin REGISTERED with, not
/// for its modid: that id is what the service keys its store, its routes and its
/// sharing by, so a directory named anything else would be a directory nothing
/// reads. A plugin is free to use its modid as its id, and one that does has one
/// name rather than two to keep in step.
/// </summary>
internal static class PluginFiles
{
    /// <summary>Where a plugin keeps what the map serves, inside its mod.</summary>
    private const string From = "plugin";

    /// <summary>
    /// Copies one plugin's files out of its mod and into the map's directory.
    ///
    /// Answers what went wrong, or null where nothing did. A plugin whose files
    /// cannot be copied still registers and still keeps rows — it simply draws
    /// nothing, which is a thing worth saying in the log rather than a reason to
    /// stop a server starting.
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
    /// A mod loaded from a folder, which is how it is developed.
    ///
    /// Copied file by file and only where the source is newer, so a plugin being
    /// worked on updates the moment the server restarts without rewriting
    /// everything it ships each time.
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
    /// A mod loaded from an archive, which is how it is installed.
    ///
    /// The archive's own timestamp decides whether anything is unpacked at all:
    /// one file's date is enough to say "this is the same mod as last start",
    /// and unpacking a plugin's whole asset directory on every start is work
    /// nobody asked for.
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
            // A directory entry has no name of its own, and nothing outside the
            // plugin's own directory in the archive is ours to unpack.
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
    /// Whether one file inside a plugin's mod is one the map serves.
    ///
    /// The entry point, whatever the plugin is written across, and what it ships
    /// for the page to show. Named rather than "everything under here", because
    /// what this writes lands in a directory the map serves out of and a rule
    /// that copied whatever it found would serve whatever a mod happened to
    /// carry.
    ///
    /// Rejects any path that climbs, which is the one thing an archive can say
    /// that a folder cannot: a zip entry is a string, and a string may be
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
