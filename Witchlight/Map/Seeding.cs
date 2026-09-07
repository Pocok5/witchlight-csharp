using System;
using System.Collections.Generic;
using Vintagestory.API.Server;

namespace Witchlight;

/// <summary>
/// Fills a fresh map by loading the chunk columns around spawn, a few at a time.
///
/// A server with nobody on it keeps almost nothing in memory, so an export of
/// what happens to be loaded covers a single chunk. Loading the square around
/// spawn gives a fresh server a map without waiting for someone to walk the
/// world. The export timer picks up everything players explore afterwards.
///
/// The columns are requested a few at a time. The rectangle form of
/// <c>LoadChunkColumnPriority</c> is documented as asynchronous but is not. The
/// server puts the rectangle on a queue that its chunk thread drains with
/// <c>loadChunkAreaBlocking</c>, which holds that thread until the whole area is
/// generated or twelve seconds have passed. A dedicated server starting up
/// tolerates that. In singleplayer the player joins in the same tick with a view
/// distance of 1152 blocks, and the thousands of columns they request pile up
/// behind the seed until the server's request queue overflows. The server then
/// clears the queue out from under its own chunk thread and dies with
/// "In queue but missed from index!".
///
/// One column at a time costs one short blocking load each, with the thread free
/// between them. Columns already in memory cost nothing, because the server
/// answers those without queueing. In singleplayer that covers most of them,
/// since the player's view distance spans this square several times over.
/// </summary>
public sealed class Seeding
{
    private readonly ICoreServerAPI _api;
    private readonly Queue<(int X, int Z)> _left;
    private readonly Action<string> _export;
    private readonly int _asked;

    /// <summary>Counts the columns requested since the last export.</summary>
    private int _since;

    private Seeding(ICoreServerAPI api, Queue<(int X, int Z)> left, Action<string> export)
    {
        _api = api;
        _left = left;
        _export = export;
        _asked = left.Count;
    }

    /// <summary>
    /// Creates a seed of the square around spawn. Returns null where the world
    /// has no spawn point to centre it on.
    /// </summary>
    public static Seeding? Around(ICoreServerAPI api, int radius, Action<string> export)
    {
        if (WorldFacts.Spawn(api) is not { } spawn)
        {
            api.Logger.Warning(
                "[witchlight] the world has no spawn point yet, so the map was not seeded — "
                + "it will fill in as players explore");
            return null;
        }

        var size = api.WorldManager.ChunkSize;
        var columns = new Queue<(int X, int Z)>(Outward(spawn.X / size, spawn.Z / size, radius));
        api.Logger.Notification(
            "[witchlight] seeding the map with {0} chunk columns around spawn, {1} at a time",
            columns.Count,
            Repair.PerStep);
        return new Seeding(api, columns, export);
    }

    /// <summary>
    /// Yields the columns of a square, nearest the middle first.
    ///
    /// The order is ring by ring rather than row by row. A watcher sees the map
    /// grow outward from spawn, and a seed cut short by a shutdown leaves a map
    /// centred on spawn rather than half a square.
    /// </summary>
    public static IEnumerable<(int X, int Z)> Outward(int centreX, int centreZ, int radius)
    {
        yield return (centreX, centreZ);
        for (var ring = 1; ring <= radius; ring++)
        {
            for (var x = centreX - ring; x <= centreX + ring; x++)
            {
                yield return (x, centreZ - ring);
                yield return (x, centreZ + ring);
            }
            // The rows above already yielded the corners.
            for (var z = centreZ - ring + 1; z <= centreZ + ring - 1; z++)
            {
                yield return (centreX - ring, z);
                yield return (centreX + ring, z);
            }
        }
    }

    /// <summary>Describes how far the seed has got.</summary>
    public string Describe() => $"seeding: {_asked - _left.Count} of {_asked} columns asked for";

    /// <summary>
    /// Requests the next few columns. Returns true while more remain.
    ///
    /// The map is written as the seed runs rather than only at the end. The
    /// server frees a loaded column nobody stands near after fifteen seconds,
    /// which is less time than a seed of any size takes, so a single export at
    /// the end would find the earliest columns gone.
    /// </summary>
    public bool Step()
    {
        for (var taken = 0; taken < Repair.PerStep && _left.Count > 0; taken++)
        {
            var (x, z) = _left.Dequeue();
            _api.WorldManager.LoadChunkColumnPriority(x, z);
            _since++;
        }

        if (_since >= ColumnsPerExport || _left.Count == 0)
        {
            _since = 0;
            _export("seed");
        }

        return _left.Count > 0;
    }


    /// <summary>
    /// Sets how many columns are requested between exports while the seed runs.
    /// This falls well inside the fifteen seconds an idle column stays in memory,
    /// and far enough apart that a seed costs a handful of writes rather than one
    /// per column.
    /// </summary>
    private const int ColumnsPerExport = 64;

}
