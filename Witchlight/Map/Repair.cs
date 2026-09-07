using System;
using System.Collections.Generic;
using Vintagestory.API.Server;

namespace Witchlight;

/// <summary>
/// Reloads chunk columns the map wants to read but whose blocks have left memory.
///
/// A map chunk outlives the blocks under it, so a column marked dirty and reached
/// after its blocks have gone cannot be read. Nothing brings one back on its own.
/// The server loads a column when somebody walks to it, and a column at the
/// trailing edge of a path nobody retraces stays a single chunk of nothing in the
/// middle of finished terrain.
///
/// This class asks the server for the column and reads it when the game reports
/// it has arrived, about a second later. Reading on the next export beat instead
/// loses a race: an untouched column ages out of memory in under ten seconds and
/// the export beat is ten seconds, so the column arrived, sat there and was gone
/// again before anything looked at it. The count of columns owed a read then
/// stood still for hours.
///
/// The scope here is one column the map had and lost. Deciding what ground the
/// map has never drawn belongs to the map service, over the channel `ModApi`
/// answers. See `ModApi.cs` and the service's own `pull.rs`.
///
/// The other way a column reads as unreadable is a sentinel in the rain
/// heightmap, which made the export believe blocks had gone when they had not.
/// See <see cref="ColumnPump"/>. The export line names which of the two cases a
/// column fell into.
///
/// Everything here runs on the server's main thread. The export drives the asking
/// from a tick listener and the game runs a load's callback as a main thread
/// task, so nothing here takes a lock. <see cref="DirtyColumns"/> does, because
/// the server's chunk events reach it from wherever it raises them.
/// </summary>
public sealed class Repair
{
    private readonly ICoreServerAPI _api;

    /// <summary>
    /// Marks the ground changed and writes it, once it is loaded.
    ///
    /// The exporter passes this in, so this class knows how to get a column back
    /// and nothing about what a map is. <see cref="Seeding"/> takes the same
    /// shape.
    /// </summary>
    private readonly Action<IReadOnlyCollection<(int, int)>> _read;

    /// <summary>
    /// Holds the columns the map wants and cannot read, in order.
    ///
    /// Each pass resumes where the last stopped, because only a few are asked for
    /// at a time and the whole list must be reached rather than the same short
    /// prefix forever. A list rather than a set: it holds only what failed, which
    /// is tens of columns, and one structure carries both membership and order.
    /// </summary>
    private readonly List<(int, int)> _owed = new();

    /// <summary>Marks where the last pass over <see cref="_owed"/> stopped.</summary>
    private int _asked;

    /// <summary>
    /// Holds columns the server has handed back that have not been read yet. A
    /// set, because a column asked for twice before either answer arrives is
    /// still one column to read.
    /// </summary>
    private readonly HashSet<(int, int)> _landed = new();

    /// <summary>Tracks whether a read of what has landed is already scheduled.</summary>
    private bool _reading;

    public Repair(ICoreServerAPI api, Action<IReadOnlyCollection<(int, int)>> read)
    {
        _api = api;
        _read = read;
    }

    /// <summary>Returns how many columns the map wants and cannot read yet.</summary>
    public int Owed => _owed.Count;

    /// <summary>
    /// Notes columns that left memory before they could be read.
    ///
    /// A column leaves this list when it reaches disk and not before. Owing a
    /// column already on the list changes nothing, so an export can report one on
    /// every beat that fails to read it.
    /// </summary>
    public void Owe(IEnumerable<(int, int)> columns)
    {
        foreach (var column in columns)
        {
            if (!_owed.Contains(column))
            {
                _owed.Add(column);
            }
        }
    }

    /// <summary>
    /// Notes columns that have reached disk. Reaching disk is what settles a
    /// column that was owed a read.
    /// </summary>
    public void Settled(IEnumerable<(int, int)> columns)
    {
        foreach (var column in columns)
        {
            var at = _owed.IndexOf(column);
            if (at < 0)
            {
                continue;
            }

            _owed.RemoveAt(at);
            if (_asked > at)
            {
                _asked--;
            }
        }
    }

    /// <summary>
    /// Asks the server to load some of the columns the map is owed. Returns how
    /// many it asked for.
    ///
    /// Each column carries a callback of its own, so the read happens about a
    /// second after the game reports the ground is there. That is well inside the
    /// life of a column nobody is standing near. A read on the next export beat
    /// is not, because the beat is ten seconds and an untouched column ages out in
    /// under that.
    ///
    /// The list is walked from where the last pass stopped, so nothing is asked
    /// for twice in a row.
    ///
    /// The beat asks for a few at a time and the command asks for more. A chunk
    /// load is real work for the server, and healing the map must not cost a
    /// stutter everybody feels.
    /// </summary>
    public int Ask(int most)
    {
        var take = Math.Min(most, _owed.Count);
        for (var i = 0; i < take; i++)
        {
            var column = _owed[(_asked + i) % _owed.Count];
            _api.WorldManager.LoadChunkColumnPriority(
                column.Item1,
                column.Item2,
                new ChunkLoadOptions { OnLoaded = () => Landed(column) });
        }

        _asked = _owed.Count == 0 ? 0 : (_asked + take) % _owed.Count;
        return take;
    }

    /// <summary>
    /// Records that one column has arrived and schedules a read of everything
    /// that has arrived.
    ///
    /// A handful of columns are asked for together and their callbacks land
    /// within a few ticks. A read apiece would cost a full export apiece, so the
    /// first column to land schedules the read and the rest join it.
    /// </summary>
    private void Landed((int, int) column)
    {
        _landed.Add(column);
        if (_reading)
        {
            return;
        }

        _reading = true;
        _api.Event.RegisterCallback(_ => ReadWhatLanded(), SettleMs);
    }

    /// <summary>Reads the ground that has arrived and clears the landed set.</summary>
    private void ReadWhatLanded()
    {
        _reading = false;
        if (_landed.Count == 0)
        {
            return;
        }

        var landed = new List<(int, int)>(_landed);
        _landed.Clear();
        _read(landed);
    }

    /// <summary>
    /// Sets how long the read waits after the first column of a batch lands.
    ///
    /// One second is long enough for the rest of the batch to arrive and short
    /// enough that the first column is still in memory when the last one gets
    /// there. An untouched column ages out in under ten seconds.
    /// </summary>
    private const int SettleMs = 1000;

    /// <summary>
    /// Sets how many columns the server may be asked to load at once.
    ///
    /// Each column is a short blocking load on the server's chunk thread, so this
    /// bounds how long that thread is held in one go. Four is a fraction of the
    /// thirty columns it generates per tick on its own. Asking for more than a
    /// handful risks more than slowness: the server queues the requests for its
    /// chunk thread, and a queue overrun clears itself out from under that thread
    /// and takes the server down. <see cref="Seeding"/> uses the same number.
    /// </summary>
    public const int PerStep = 4;

    /// <summary>
    /// Sets how often columns may be asked for. The gap leaves the chunk thread
    /// free for the players whose own chunks are queued behind these.
    ///
    /// This runs on a clock of its own rather than on the export beat. Getting a
    /// column back and writing what it says are different jobs at different
    /// speeds. Tying them together made a map rebuild at whatever rate the
    /// operator had chosen to write the disk, which took sixty-five minutes for a
    /// map this fills in seven.
    /// </summary>
    public const int StepIntervalMs = 250;

    /// <summary>
    /// Sets how many columns a forced export asks for. An operator running the
    /// command is waiting for an answer, so the number is larger than the beat's.
    ///
    /// It stays bounded. A map that has lost thousands of columns is a bug to fix
    /// rather than a queue to flood, and the next command asks for the next few
    /// hundred.
    /// </summary>
    public const int PerCommand = 256;
}
