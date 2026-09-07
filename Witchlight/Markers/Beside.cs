using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace Witchlight;

/// <summary>
/// Stores one fact per waypoint that the game does not keep, in the savegame
/// beside the waypoints.
///
/// A waypoint carries the fields the game needs. Sharing and the block a marker
/// was put on are this mod's ideas, so this mod keeps those answers. They live in
/// the savegame rather than in files of their own because they are properties of a
/// waypoint, and two stores that can be lost separately can disagree. A world that
/// loses its waypoints loses these with them.
///
/// This class holds the mechanism and its users hold the meaning. Reading a store
/// back, writing it when it has changed, and dropping what it says about waypoints
/// that no longer exist are the same three jobs whatever is being remembered.
///
/// **Only an answer somebody actually gave is stored.** A waypoint nothing has
/// been said about is absent, and its owner reads that absence as the operator's
/// default or as nothing known. So a store holds one entry per answer rather than
/// one per marker, and stays empty on a server where nobody uses the map.
/// </summary>
public sealed class Beside<T>
{
    private readonly string _key;

    /// <summary>What is known about each waypoint, by guid.</summary>
    private readonly Dictionary<string, T> _held;

    /// <summary>True when something has changed since the last write. A save that
    ///  would store what is already stored does not write.</summary>
    private bool _unsaved;

    private Beside(string key, Dictionary<string, T> held)
    {
        _key = key;
        _held = held;
    }

    /// <summary>
    /// An empty store, in which every waypoint reads as the absence of an answer.
    ///
    /// The mod holds this before the world is up, when the savegame cannot be read.
    /// A null store would put a check at every use.
    /// </summary>
    public static Beside<T> Empty(string key) =>
        new(key, new Dictionary<string, T>(StringComparer.Ordinal));

    /// <summary>How many waypoints this knows something about. Reported by status.</summary>
    public int Count => _held.Count;

    /// <summary>
    /// Reads back what a previous run stored. Returns an empty store when the bytes
    /// cannot be read, so every waypoint falls back to the absence of an answer.
    /// </summary>
    public static Beside<T> Read(ICoreServerAPI api, string key, string called)
    {
        try
        {
            var stored = api.WorldManager.SaveGame.GetData(key);
            if (stored is null || stored.Length == 0)
            {
                return Empty(key);
            }

            var read = JsonConvert.DeserializeObject<Dictionary<string, T>>(
                Encoding.UTF8.GetString(stored));
            return read is null
                ? Empty(key)
                : new Beside<T>(key, new Dictionary<string, T>(read, StringComparer.Ordinal));
        }
        catch (Exception error)
        {
            api.Logger.Warning("[witchlight] could not read {0}: {1}", called, error.Message);
            return Empty(key);
        }
    }

    /// <summary>
    /// Writes what is held, when it is not what is already stored.
    ///
    /// Drops answers about waypoints that no longer exist first, so deleting a
    /// marker eventually takes what was said about it and the store does not grow
    /// by one entry for every marker the server has ever had.
    /// </summary>
    /// <param name="live">
    /// The waypoints that still exist, or null when they could not be read. Null
    /// does not mean there are none, so this forgets nothing without the live list.
    /// Treating null as an empty list would forget every answer on the server the
    /// first time a save landed while the map layer was down.
    /// </param>
    public void Write(ICoreServerAPI api, IEnumerable<Waypoint>? alive, string called)
    {
        if (alive is not null)
        {
            Forget(alive);
        }

        if (!_unsaved)
        {
            return;
        }

        try
        {
            api.WorldManager.SaveGame.StoreData(
                _key, Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(_held)));
            _unsaved = false;
        }
        catch (Exception error)
        {
            api.Logger.Warning("[witchlight] could not store {0}: {1}", called, error.Message);
        }
    }

    /// <summary>Returns what is known about one waypoint, or null.</summary>
    public bool Knows(string? guid, out T held)
    {
        held = default!;
        return !string.IsNullOrEmpty(guid) && _held.TryGetValue(guid, out held!);
    }

    /// <summary>Records what is known about one waypoint. An answer already held
    ///  is not a change and does not trigger a write.</summary>
    public void Say(string? guid, T said)
    {
        if (string.IsNullOrEmpty(guid))
        {
            return;
        }
        if (_held.TryGetValue(guid, out var known) && EqualityComparer<T>.Default.Equals(known, said))
        {
            return;
        }

        _held[guid] = said;
        _unsaved = true;
    }

    /// <summary>Returns everything this knows, by waypoint guid, for a feed to
    ///  hand on.</summary>
    public IReadOnlyDictionary<string, T> All => _held;

    /// <summary>Drops what is stored about waypoints that no longer exist.</summary>
    private void Forget(IEnumerable<Waypoint> alive)
    {
        var live = new HashSet<string>(
            alive.Select(waypoint => waypoint?.Guid ?? "").Where(guid => guid.Length > 0),
            StringComparer.Ordinal);

        foreach (var gone in _held.Keys.Where(guid => !live.Contains(guid)).ToList())
        {
            _held.Remove(gone);
            _unsaved = true;
        }
    }
}
