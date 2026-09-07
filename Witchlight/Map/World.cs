using System;
using System.IO;
using Newtonsoft.Json;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace Witchlight;

/// <summary>Holds the facts about the world that do not change while it runs.</summary>
public class WorldFacts
{
    /// <summary>Returns the path a world's facts are filed at.</summary>
    public static string PathIn(string exports) => Path.Combine(exports, "world.json");

    /// <summary>
    /// The block position the map counts coordinates from.
    ///
    /// Vintage Story shows coordinates relative to world spawn everywhere a
    /// player sees them, including the in-game map and the position readout. The
    /// world itself is a million blocks across with spawn near the middle, so a
    /// map showing absolute positions agrees with nothing the player can compare
    /// it to.
    /// </summary>
    public int SpawnX { get; set; }
    public int SpawnY { get; set; }
    public int SpawnZ { get; set; }

    public string Name { get; set; } = "";

    /// <summary>
    /// Identifies the savegame, so a map found on its own can be matched to the
    /// world that wrote it rather than the world that happens to be starting.
    /// This is empty for a map written by an older build.
    /// </summary>
    public string Id { get; set; } = "";

    /// <summary>
    /// The y the world's oceans sit at.
    ///
    /// The renderer uses this rather than the page. How much of the season's
    /// colour a block takes depends on its height above sea level, which is how
    /// the game keeps a mountainside from turning autumn with the valley.
    /// </summary>
    public int SeaLevel { get; set; }


    /// <summary>
    /// Returns the position the world counts coordinates from.
    ///
    /// The game has two spawn points. The world manager's is the one an admin set
    /// explicitly, which on most worlds is unset, and its getter dereferences
    /// that null rather than returning empty. The world accessor's is the one the
    /// world counts from, near the middle of the map, and every coordinate a
    /// player reads is relative to it.
    ///
    /// The result is null until the world has finished loading. A spawn of zero
    /// is a valid coordinate, so null must not be stood in for.
    /// </summary>
    public static (int X, int Y, int Z)? Spawn(ICoreServerAPI api)
    {
        try
        {
            var at = api.World?.DefaultSpawnPosition?.AsBlockPos;
            return at is null ? null : (at.X, at.Y, at.Z);
        }
        catch (NullReferenceException)
        {
            // The world has no spawn point yet. Every caller expects null.
            return null;
        }
    }

    /// <summary>
    /// Returns the name of the world a map on disk was written for, or null where
    /// it does not say. This is what tells a map found loose in a folder apart
    /// from the world starting now.
    /// </summary>
    public static string? NameIn(string exports)
    {
        try
        {
            var path = PathIn(exports);
            return File.Exists(path)
                ? JsonConvert.DeserializeObject<WorldFacts>(File.ReadAllText(path))?.Name
                : null;
        }
        catch (Exception)
        {
            // An unreadable file says nothing about which world wrote the map,
            // which is the same answer as a file that omits the name.
            return null;
        }
    }

    /// <summary>
    /// Returns one line for `/witchlight status` saying what the map counts from.
    ///
    /// Two things must hold and neither is visible in game. The world must have a
    /// spawn point, and that spawn point must have reached the map. A map
    /// counting from absolute zero looks like one counting from spawn until
    /// somebody compares a coordinate against their own screen.
    /// </summary>
    public static string Describe(ICoreServerAPI api, string exports)
    {
        if (Spawn(api) is not { } spawn)
        {
            return "counts from: absolute zero — the world has no readable spawn point";
        }

        return $"counts from: spawn at {spawn.X}, {spawn.Z}"
            + (File.Exists(PathIn(exports))
                ? ""
                : "  (world.json not written — the map counts from absolute zero)");
    }

    /// <summary>
    /// Writes the world's facts where the map service will find them. Returns
    /// true when it wrote.
    ///
    /// Call this once the world is ready rather than when the mod starts, because
    /// spawn is not known that early. Nothing is written when spawn cannot be
    /// read. A file saying spawn is the origin reads like a world whose spawn is
    /// the origin, while a missing file is a state the map service reports.
    ///
    /// The return value lets a caller that asked too early ask again.
    /// </summary>
    public static bool Write(ICoreServerAPI api, string exports)
    {
        try
        {
            var spawn = Spawn(api);
            if (spawn is null)
            {
                return false;
            }

            var facts = new WorldFacts
            {
                SpawnX = spawn.Value.X,
                SpawnY = spawn.Value.Y,
                SpawnZ = spawn.Value.Z,
                Name = api.WorldManager.SaveGame?.WorldName ?? "",
                Id = api.WorldManager.SaveGame?.SavegameIdentifier ?? "",
                SeaLevel = api.World.SeaLevel,
            };

            Disk.Write(
                PathIn(exports), JsonConvert.SerializeObject(facts));

            api.Logger.Notification(
                "[witchlight] the map counts from spawn at {0}, {1}", spawn.Value.X, spawn.Value.Z);
            return true;
        }
        catch (Exception error)
        {
            api.Logger.Warning("[witchlight] could not write world.json: {0}", error);
            return false;
        }
    }
}
