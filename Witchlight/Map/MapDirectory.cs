using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace Witchlight;

/// <summary>
/// Works out which directory a world's map lives in.
///
/// A dedicated server runs one world out of one data path, so its map sits in
/// one folder. A client runs every save out of the same data path, and one
/// folder for all of them means the second world writes its terrain into the
/// first world's map at the same region coordinates. That produces a map of two
/// worlds at once with nothing saying so. It also rewrites the palette and the
/// block names in full on every switch between a world with no mods and a world
/// with fifty, even though those files describe the mod set rather than the
/// world.
///
/// So each world may have a directory of its own, named after it. Nothing is
/// shared between them, including files that happen to be identical. A file
/// written once and left alone costs nothing to keep.
/// </summary>
public static class MapDirectory
{
    /// <summary>
    /// Returns the directory name this world's map is filed under.
    ///
    /// The name combines the world's own name, so a directory listing reads as a
    /// list of worlds, with enough of the savegame's identifier to tell two of
    /// them apart. Worlds called "New World" are common, and two of them sharing
    /// a directory is the failure this exists to prevent.
    /// </summary>
    public static string NameFor(string? worldName, string? savegameId, int seed)
    {
        var named = Sanitised(worldName);
        // The savegame identifier is optional in the format, so a world made by
        // an older build may carry none. The name and the seed together are as
        // fixed as the world is, so they stand in for it.
        var unique = string.IsNullOrWhiteSpace(savegameId)
            ? $"{worldName}:{seed}"
            : savegameId!;
        return $"{named}-{Short(unique)}";
    }

    /// <summary>Returns the directory name for the world a server is running.</summary>
    public static string NameFor(ICoreServerAPI api)
    {
        var save = api.WorldManager.SaveGame;
        return NameFor(save?.WorldName, save?.SavegameIdentifier, save?.Seed ?? 0);
    }

    /// <summary>Settles the map directory for the world a server is running.</summary>
    public static string Settle(ICoreServerAPI api, string baseDir, bool perWorld)
    {
        var save = api.WorldManager.SaveGame;
        return Settle(
            baseDir, save?.WorldName, save?.SavegameIdentifier, save?.Seed ?? 0, perWorld,
            api.Logger);
    }

    /// <summary>
    /// Turns a name a person typed into one a filesystem will take.
    ///
    /// This replaces every path separator and every character Windows refuses,
    /// then strips leading and trailing dots and spaces. Windows drops those last
    /// silently, and a directory it renames behind us is a map that goes missing
    /// on the next start.
    /// </summary>
    private static string Sanitised(string? name)
    {
        var refused = new HashSet<char>(Path.GetInvalidFileNameChars()) { '/', '\\', ':' };
        var kept = new string((name ?? "")
            .Select(letter => refused.Contains(letter) || char.IsControl(letter) ? '_' : letter)
            .ToArray())
            .Trim()
            .Trim('.');

        // A world named entirely in refused characters still needs a directory.
        return kept.Length == 0 ? "world" : kept[..Math.Min(kept.Length, 64)].TrimEnd();
    }

    /// <summary>Hashes a stable string down to eight hex characters.</summary>
    private static string Short(string of)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(of));
        return Convert.ToHexString(digest)[..8].ToLowerInvariant();
    }

    /// <summary>
    /// Returns where this world's map goes, moving a loose map aside first.
    ///
    /// A map sitting loose in the folder belongs to whichever world last ran, and
    /// turning this setting on must not leave it to be written over by the next
    /// one. It moves into a directory of its own before anything else happens:
    /// this world's, where `world.json` says it is this world's, and one named
    /// after its own world otherwise. Nothing is deleted and nothing is merged.
    /// </summary>
    public static string Settle(
        string baseDir, string? worldName, string? savegameId, int seed, bool perWorld, ILogger log)
    {
        if (!perWorld)
        {
            return baseDir;
        }

        var mine = Path.Combine(baseDir, NameFor(worldName, savegameId, seed));
        MoveLooseMapAside(baseDir, mine, worldName, log);
        return mine;
    }

    /// <summary>
    /// Moves a map lying loose in the folder into a directory of its own.
    ///
    /// This runs only when a loose map exists and its destination does not. A
    /// folder that already holds the destination directory has been through this,
    /// and moving on top of it would be the merge this prevents.
    /// </summary>
    private static void MoveLooseMapAside(
        string baseDir, string mine, string? worldName, ILogger log)
    {
        var loose = Path.Combine(baseDir, "palette.json");
        if (!File.Exists(loose))
        {
            return;
        }

        var whose = LooseMapBelongsTo(baseDir, mine, worldName);
        if (Directory.Exists(whose))
        {
            log.Warning(
                "[witchlight] there is a map in {0} and a map in {1}. The loose one has been left "
                + "where it is rather than written over the other; move or delete it by hand.",
                baseDir,
                whose);
            return;
        }

        try
        {
            var moved = Directory.CreateDirectory(whose + ".moving");
            foreach (var file in Directory.EnumerateFiles(baseDir))
            {
                File.Move(file, Path.Combine(moved.FullName, Path.GetFileName(file)));
            }
            foreach (var folder in Directory.EnumerateDirectories(baseDir))
            {
                var name = Path.GetFileName(folder);
                // Skip the directory being built and any other world's map.
                if (folder == moved.FullName || Directory.Exists(Path.Combine(folder, "columns")))
                {
                    continue;
                }
                Directory.Move(folder, Path.Combine(moved.FullName, name));
            }
            Directory.Move(moved.FullName, whose);

            log.Notification(
                "[witchlight] each world now keeps its own map, so the one that was in {0} "
                + "has been moved to {1}",
                baseDir,
                whose);
        }
        catch (Exception error)
        {
            log.Error(
                "[witchlight] could not move the map in {0} into {1}, so it has been left alone: "
                + "{2}",
                baseDir,
                whose,
                error);
        }
    }

    /// <summary>
    /// Returns the directory a loose map belongs in.
    ///
    /// `world.json` names the world the map was written for. Where that is this
    /// world, the map goes into this world's directory. Where it names another,
    /// the map goes under that name alone, because this half cannot work out
    /// another world's savegame identifier.
    /// </summary>
    public static string LooseMapBelongsTo(string baseDir, string mine, string? worldName)
    {
        var named = WorldFacts.NameIn(baseDir);
        return string.IsNullOrWhiteSpace(named) || named == worldName
            ? mine
            : Path.Combine(baseDir, Sanitised(named) + "-unknown");
    }
}
