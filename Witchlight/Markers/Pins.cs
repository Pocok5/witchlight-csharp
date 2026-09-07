using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace Witchlight;

/// <summary>
/// Stores which markers each player keeps in sight on their own map.
///
/// The game holds a pinned waypoint against the edge of the map instead of letting
/// it scroll off. It keeps the flag on the waypoint, which works for the markers
/// the game thinks a player has, meaning their own. Everybody else's arrive on a
/// client as temporary waypoints this mod lays down, rebuilt from what the server
/// sends every time the map opens, so a flag on those has nowhere to live.
///
/// So the answer comes in two halves and this class owns both, since two places
/// answering one question give two answers. A marker somebody owns is answered by
/// the waypoint itself, where the game's own map dialog writes it. Anybody else's
/// is answered from the store here, kept beside the waypoints in the savegame for
/// the same reason <see cref="Visibility"/> is. A world that loses its waypoints
/// loses what was said about them at the same moment.
///
/// **A pin is one person's, never everyone's.** Pinning somebody's marker puts it
/// on the pinner's map and on no other.
/// </summary>
public sealed class Pins
{
    private const string SaveKey = "witchlight:markerpins";

    /// <summary>The keys of other people's markers each player keeps in sight, by
    ///  uid. A player's own pins live on the waypoint instead.</summary>
    private readonly Dictionary<string, HashSet<string>> _kept;

    /// <summary>True when something has changed since the last write. A save that
    ///  would store what is already stored does not write.</summary>
    private bool _unsaved;

    private Pins(Dictionary<string, HashSet<string>> kept)
    {
        _kept = kept;
    }

    /// <summary>
    /// An empty store, in which every map shows only what the game put on it.
    ///
    /// The mod holds this before the world is up, when the savegame cannot be read.
    /// A null store would put a check at every use.
    /// </summary>
    public static Pins Empty => new(new Dictionary<string, HashSet<string>>(StringComparer.Ordinal));

    /// <summary>How many of other people's markers are pinned, across everybody.
    ///  Reported by status.</summary>
    public int Decisions => _kept.Values.Sum(kept => kept.Count);

    /// <summary>
    /// Reads back what a previous run stored. Returns an empty store when the bytes
    /// cannot be read. A player can put a lost pin back with one press, and a map
    /// showing too little is better than one showing the wrong thing.
    /// </summary>
    public static Pins Read(ICoreServerAPI api)
    {
        try
        {
            var stored = api.WorldManager.SaveGame.GetData(SaveKey);
            if (stored is null || stored.Length == 0)
            {
                return Empty;
            }

            var read = JsonConvert.DeserializeObject<Dictionary<string, List<string>>>(
                Encoding.UTF8.GetString(stored));
            if (read is null)
            {
                return Empty;
            }

            var kept = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            foreach (var (uid, keys) in read)
            {
                kept[uid] = new HashSet<string>(keys ?? new List<string>(), StringComparer.Ordinal);
            }
            return new Pins(kept);
        }
        catch (Exception error)
        {
            api.Logger.Warning("[witchlight] could not read marker pins: {0}", error.Message);
            return Empty;
        }
    }

    /// <summary>
    /// Writes the pins, when they are not the ones already stored.
    ///
    /// Drops pins on markers that no longer exist first, so deleting a marker
    /// eventually takes everyone's pin on it and the store does not grow by one
    /// entry for every marker the server has ever had.
    /// </summary>
    /// <param name="live">
    /// The waypoints that still exist, or null when they could not be read. Null
    /// does not mean there are none, so this forgets nothing without the live list.
    /// Treating null as an empty list would drop every pin on the server the first
    /// time a save landed while the map layer was down.
    /// </param>
    public void Write(ICoreServerAPI api, IEnumerable<Waypoint>? alive)
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
            var said = _kept.ToDictionary(
                held => held.Key, held => held.Value.ToList(), StringComparer.Ordinal);
            api.WorldManager.SaveGame.StoreData(
                SaveKey, Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(said)));
            _unsaved = false;
        }
        catch (Exception error)
        {
            api.Logger.Warning("[witchlight] could not store marker pins: {0}", error.Message);
        }
    }

    /// <summary>
    /// Returns true when this player keeps this marker in sight.
    ///
    /// Reads their own marker's pin off the waypoint, which is the flag the game's
    /// own map dialog sets and reads, and anybody else's from the store. One
    /// function, so the two halves cannot disagree.
    /// </summary>
    public bool Kept(Waypoint waypoint, string uid)
    {
        if (string.IsNullOrEmpty(uid))
        {
            return false;
        }
        if (waypoint.OwningPlayerUid == uid)
        {
            return waypoint.Pinned;
        }
        return _kept.TryGetValue(uid, out var theirs) && theirs.Contains(Markers.Key(waypoint));
    }

    /// <summary>
    /// Records that this player does, or no longer does, keep this marker in sight.
    ///
    /// Changes and resends their own waypoint, which makes the pin show on the map
    /// they have open. Writes anybody else's to the store, which reaches them with
    /// the next share, because a temporary waypoint is laid down again from what
    /// the server sends rather than edited where it lies.
    /// </summary>
    public void Choose(ICoreServerAPI api, Waypoint waypoint, string uid, bool keep)
    {
        if (string.IsNullOrEmpty(uid))
        {
            return;
        }

        if (waypoint.OwningPlayerUid == uid)
        {
            if (waypoint.Pinned == keep)
            {
                return;
            }
            waypoint.Pinned = keep;
            Markers.Resend(api, uid);
            return;
        }

        var key = Markers.Key(waypoint);
        if (!_kept.TryGetValue(uid, out var theirs))
        {
            if (!keep)
            {
                return;
            }
            theirs = new HashSet<string>(StringComparer.Ordinal);
            _kept[uid] = theirs;
        }

        var moved = keep ? theirs.Add(key) : theirs.Remove(key);
        if (!moved)
        {
            return;
        }
        if (theirs.Count == 0)
        {
            _kept.Remove(uid);
        }
        _unsaved = true;
    }

    /// <summary>
    /// Returns every marker each player keeps in sight, by uid, for the map service
    /// to hand each of them their own.
    ///
    /// Returns both halves in one answer, since the page asking has one question.
    /// Reads a player's own pins off the waypoints and the rest from the store.
    /// </summary>
    public Dictionary<string, List<string>> Everyones(IEnumerable<Waypoint> alive)
    {
        var said = _kept.ToDictionary(
            held => held.Key, held => held.Value.ToList(), StringComparer.Ordinal);

        foreach (var waypoint in alive)
        {
            var uid = waypoint?.OwningPlayerUid;
            if (waypoint is null || !waypoint.Pinned || string.IsNullOrEmpty(uid))
            {
                continue;
            }

            if (!said.TryGetValue(uid, out var theirs))
            {
                theirs = new List<string>();
                said[uid] = theirs;
            }
            theirs.Add(Markers.Key(waypoint));
        }

        return said;
    }

    /// <summary>Drops pins on markers that no longer exist.</summary>
    private void Forget(IEnumerable<Waypoint> alive)
    {
        var live = new HashSet<string>(
            alive.Where(waypoint => waypoint is not null).Select(Markers.Key),
            StringComparer.Ordinal);

        foreach (var (uid, theirs) in _kept.ToList())
        {
            if (theirs.RemoveWhere(key => !live.Contains(key)) == 0)
            {
                continue;
            }
            _unsaved = true;
            if (theirs.Count == 0)
            {
                _kept.Remove(uid);
            }
        }
    }
}
