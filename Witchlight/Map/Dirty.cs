using System.Collections.Generic;
using Vintagestory.API.Common;

namespace Witchlight;

/// <summary>
/// Tracks which chunk columns need their surface read again.
///
/// The server raises ChunkDirty for every chunk whose blocks moved, and also for
/// every chunk it loads or generates. Loading is not a change, so a chunk already
/// in the export is left alone until something happens to it. Without that
/// distinction a player walking a circuit would re-read the whole route each time
/// they crossed it.
///
/// The set holds one coordinate pair per changed column, so a quiet server
/// accumulates nothing and triggers an empty export.
///
/// A column is in one of two states here: changed and waiting to be read, or
/// already on disk. A column that is wanted but cannot be read is not a state of
/// this bookkeeping. It is work the server has to be asked for, which belongs to
/// <see cref="Repair"/>.
/// </summary>
public sealed class DirtyColumns
{
    private readonly HashSet<(int, int)> _dirty = new();
    private readonly HashSet<(int, int)> _exported = new();

    private readonly object _gate = new();

    /// <summary>
    /// Returns how many columns are waiting. ChunkDirty is not documented to
    /// arrive on the main thread and the exporter drains this from a tick
    /// listener, so every access takes the lock.
    /// </summary>
    public int Count
    {
        get { lock (_gate) { return _dirty.Count; } }
    }

    /// <summary>
    /// Notes a chunk the server has marked dirty. The vertical position is
    /// dropped. The export is a surface, and a change anywhere in the column can
    /// move it.
    /// </summary>
    public void Mark(int chunkX, int chunkZ, EnumChunkDirtyReason reason)
    {
        lock (_gate)
        {
            if (reason == EnumChunkDirtyReason.NewlyLoaded && _exported.Contains((chunkX, chunkZ)))
            {
                return;
            }

            _dirty.Add((chunkX, chunkZ));
        }
    }

    /// <summary>Marks these columns whatever their history, as a forced export needs.</summary>
    public void MarkAll(IEnumerable<(int, int)> columns) => AddAll(_dirty, columns);

    /// <summary>
    /// Marks columns that were already in memory before anything was watching.
    ///
    /// The server loads a square of chunks around spawn while starting, before
    /// the mod has anywhere to write, so those columns raise their one ChunkDirty
    /// into nothing. The server then holds them for as long as it runs, so they
    /// never raise another, and nothing else asks for them either, because a
    /// request for a column already in memory queues nothing. The result was a
    /// square hole at spawn that no amount of walking would fill in.
    ///
    /// Call this with what is loaded at the moment the export starts watching.
    /// Columns already on disk are skipped.
    /// </summary>
    public void MarkUnexported(IEnumerable<(int, int)> loaded)
    {
        lock (_gate)
        {
            foreach (var column in loaded)
            {
                if (!_exported.Contains(column))
                {
                    _dirty.Add(column);
                }
            }
        }
    }

    /// <summary>
    /// Records columns that are already in an export on disk, so loading them
    /// again is not mistaken for a change. Call this once at start with what a
    /// previous run left behind.
    /// </summary>
    public void Seed(IEnumerable<(int, int)> exported) => AddAll(_exported, exported);

    /// <summary>
    /// Returns the waiting columns and clears them. Anything marked while the
    /// export runs stays for the next one, so a change arriving mid-export is
    /// delayed rather than lost.
    /// </summary>
    public HashSet<(int, int)> Take()
    {
        lock (_gate)
        {
            var taken = new HashSet<(int, int)>(_dirty);
            _dirty.Clear();
            return taken;
        }
    }

    /// <summary>
    /// Returns up to <paramref name="most"/> of the waiting columns and clears
    /// those. The rest wait for the next tick. Reading a few chunks per beat
    /// bounds what forty people building at once cost the game thread.
    /// </summary>
    public List<(int, int)> TakeUpTo(int most)
    {
        lock (_gate)
        {
            var taken = new List<(int, int)>(System.Math.Min(most, _dirty.Count));
            foreach (var column in _dirty)
            {
                if (taken.Count >= most)
                {
                    break;
                }
                taken.Add(column);
            }
            foreach (var column in taken)
            {
                _dirty.Remove(column);
            }
            return taken;
        }
    }

    /// <summary>
    /// Drops columns from the waiting pile without reading them. This suits a
    /// chunk whose record a block event already patched.
    /// </summary>
    public void Drop(IEnumerable<(int, int)> columns)
    {
        lock (_gate)
        {
            foreach (var column in columns)
            {
                _dirty.Remove(column);
            }
        }
    }

    /// <summary>
    /// Puts columns back on the waiting pile, for an export that failed or that
    /// found a chunk not yet ready to read.
    /// </summary>
    public void Restore(IEnumerable<(int, int)> columns) => AddAll(_dirty, columns);

    /// <summary>
    /// Drops the record of having exported these columns.
    ///
    /// This suits a column that left memory before it could be read. Such a
    /// column must not be held dirty, because a chunk that is gone cannot be
    /// read, and the set would stay permanently non-empty. That one state costs
    /// an idle server a full pass over the map every tick. Forgetting the export
    /// makes the column readable again, because loading it raises ChunkDirty and
    /// with no export on record that counts as a change.
    ///
    /// Forgetting alone does not fill the hole, because nothing will load that
    /// column. The exporter tells <see cref="Repair"/> at the same moment.
    /// </summary>
    public void Forget(IEnumerable<(int, int)> columns)
    {
        lock (_gate)
        {
            foreach (var column in columns)
            {
                _exported.Remove(column);
            }
        }
    }

    /// <summary>Records that these columns are now in the export on disk.</summary>
    public void Exported(IEnumerable<(int, int)> columns) => AddAll(_exported, columns);

    /// <summary>Adds columns to one of the two sets, under the lock.</summary>
    private void AddAll(HashSet<(int, int)> into, IEnumerable<(int, int)> columns)
    {
        lock (_gate)
        {
            foreach (var column in columns)
            {
                into.Add(column);
            }
        }
    }
}
