using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace Witchlight;

/// <summary>
/// Sends the surface of the world to the map service.
///
/// This holds the whole export: what has moved since last time, what to re-read,
/// and the sending of it. It runs on the server's own tick, so it must stay
/// readable enough to confirm it is cheap.
///
/// Terrain travels over the service's API channel and into the service's
/// database, which is the one place the map is kept. This side holds a checksum
/// per chunk, which is enough to tell a chunk loading again from one that
/// changed. For chunks somebody is building in it also holds the record itself,
/// so a block placed or broken costs one column read and a small post rather
/// than a thousand reads.
///
/// The work runs in two lanes on two clocks.
///
/// The fast lane runs every quarter second. A block a player places or breaks
/// patches one column of its chunk's record in memory, and the chunk goes on the
/// next post without a re-read. Chunks the server marked dirty for any other
/// reason are re-read whole, a bounded few per beat, so the game thread pays a
/// slice of each tick rather than all of it.
///
/// The slow lane runs on `export_interval_ms`. It asks where the year has
/// reached for every chunk the map holds and sends the season of any that
/// crossed a month. It also re-reads whole every chunk the fast lane patched
/// rather than read, which catches anything no block event described, such as
/// water, growth, a fire, or a tree felled.
/// </summary>
public sealed class Exporter
{
    private readonly ICoreServerAPI _api;
    private readonly string _exports;
    private readonly MapService _service;

    /// <summary>Holds chunks whose blocks moved and whose surface is to be read again.</summary>
    private readonly DirtyColumns _dirty = new();

    /// <summary>
    /// Holds chunks whose blocks moved since the slow lane last looked, whether
    /// or not the fast lane has answered for them since. The slow lane's
    /// catch-all reads these whole.
    /// </summary>
    private readonly DirtyColumns _touched = new();

    /// <summary>
    /// Tracks the columns the map is owed and gets them back. See
    /// <see cref="Repair"/>.
    /// </summary>
    private readonly Repair _repair;

    /// <summary>
    /// Holds what the service has for each chunk: the checksum of its record and
    /// the season it was sent with. This is seeded from the service at start and
    /// updated with every post, so a chunk loading again is told from one that
    /// changed without keeping a copy of the ground on this side.
    /// </summary>
    private readonly Dictionary<(int, int), Known> _known = new();

    /// <summary>
    /// Holds the records of the chunks most recently read, so a block event can
    /// patch one column rather than re-read a thousand. The count is bounded
    /// because a record is six kilobytes and a long-running server loads every
    /// chunk in the world sooner or later.
    /// </summary>
    private readonly Recent _recent = new(RecentChunks);

    /// <summary>Holds chunks whose record a block event patched, ready to send.</summary>
    private readonly HashSet<(int, int)> _patched = new();

    /// <summary>Holds chunks whose season the slow lane found had turned, waiting for the next post.</summary>
    private readonly Dictionary<(int, int), byte> _turned = new();

    /// <summary>The buffer one chunk is read into. It is reused, because every chunk is the same size.</summary>
    private readonly ColumnPump.Surface _surface;

    /// <summary>Tracks whether the service has answered what it holds.</summary>
    private bool _synced;
    private bool _syncing;

    /// <summary>Tracks whether spawn has reached the map service.</summary>
    private bool _wroteWorldFacts;

    private readonly System.Func<int, bool> _shows;
    private readonly Microblocks _chiselled;

    private readonly record struct Known(uint Crc, byte Season);

    public Exporter(ICoreServerAPI api, string exports, MapService service, System.Func<int, bool> shows)
    {
        _api = api;
        _exports = exports;
        _service = service;
        _shows = shows;
        _chiselled = Microblocks.In(api.World);
        var edge = api.WorldManager.ChunkSize;
        _surface = new ColumnPump.Surface(edge * edge);

        // A recovered column was asked for so it could be sent. It is marked
        // here because a column the server already held raises no ChunkDirty
        // when asked for again, so the callback is the only signal it arrived.
        _repair = new Repair(api, columns => _dirty.MarkAll(columns));

        // The server loaded the square of chunks around spawn before this class
        // existed to hear about it. Those are held for the life of the server,
        // so their one ChunkDirty has already passed.
        _dirty.MarkUnexported(LoadedColumns(api));
    }

    /// <summary>Sets how many chunks the fast lane may re-read whole in one beat.</summary>
    private const int ReadsPerBeat = 8;

    /// <summary>Sets how many chunks' records are held for patching. This costs twelve megabytes at most.</summary>
    private const int RecentChunks = 2048;

    /// <summary>Returns how many columns are waiting to be re-read.</summary>
    public int Waiting => _dirty.Count;

    /// <summary>Returns how many columns the map wants and cannot read yet.</summary>
    public int Withheld => _repair.Owed;

    /// <summary>Returns how many chunks the map holds, as far as this side knows.</summary>
    public int Mapped => _known.Count;

    /// <summary>Notes a chunk the server has marked dirty.</summary>
    public void Mark(int chunkX, int chunkZ, EnumChunkDirtyReason reason)
    {
        _dirty.Mark(chunkX, chunkZ, reason);
        _touched.Mark(chunkX, chunkZ, reason);
    }

    /// <summary>
    /// Notes a block a player placed or broke. Where the chunk's record is held,
    /// this reads one column again and patches the record. Otherwise it marks the
    /// chunk for a whole read like any other change.
    ///
    /// Call this on the main thread only. The game raises these events there and
    /// everything this touches lives there.
    /// </summary>
    public void Touched(BlockPos at)
    {
        var edge = _api.WorldManager.ChunkSize;
        var chunk = ColumnPump.ChunkOf(at.X, at.Z, edge);

        if (!_recent.TryGet(chunk, out var record))
        {
            _dirty.Mark(chunk.X, chunk.Z, EnumChunkDirtyReason.MarkedDirty);
            return;
        }

        var entry = ColumnPump.ReadColumn(_api, at.X, at.Z, _shows, _chiselled);
        if (entry is null)
        {
            _dirty.Mark(chunk.X, chunk.Z, EnumChunkDirtyReason.MarkedDirty);
            return;
        }

        var offset = ColumnPump.OffsetOf(at.X, at.Z, edge);
        if (record.AsSpan(offset, Regions.EntryBytes).SequenceEqual(entry))
        {
            // The surface did not move, which is almost always the case
            // underground. Comparing here keeps mining off the wire.
            return;
        }

        entry.CopyTo(record, offset);
        _patched.Add(chunk);
    }

    /// <summary>
    /// Makes sure the position the world counts from has reached the map service,
    /// and keeps trying until it has.
    ///
    /// This runs when the world is ready and again on every export. The world
    /// being ready and the world having a spawn point are not guaranteed to
    /// arrive in that order. Repeating costs nothing once the facts are written,
    /// and until they are the map counts from somewhere the players do not.
    /// </summary>
    public void KeepWorldFacts()
    {
        _wroteWorldFacts = _wroteWorldFacts || WorldFacts.Write(_api, _exports);
    }

    /// <summary>
    /// Runs one beat of the fast lane, on the game thread.
    ///
    /// One post carries everything that has to reach the service now: patched
    /// chunks, a few re-read chunks, and seasons that turned. Nothing leaves the
    /// pile while a post is in flight, because the service takes one post of this
    /// kind at a time. A beat arriving during a post leaves everything for the
    /// next beat.
    /// </summary>
    public void Push()
    {
        if (!_synced)
        {
            Sync();
        }

        if (_service.TerrainBusy)
        {
            return;
        }

        var edge = _api.WorldManager.ChunkSize;
        var entries = new List<Entry>();
        var sent = new List<(int, int)>();

        // Patched chunks go first. They cost nothing to read and cover what a
        // player is standing over.
        foreach (var chunk in _patched)
        {
            if (_recent.TryGet(chunk, out var record))
            {
                entries.Add(Entry.Of(chunk, record, SeasonOf(chunk, edge)));
                sent.Add(chunk);
            }
        }
        _dirty.Drop(_patched);
        _patched.Clear();

        // Then re-read a bounded few whole.
        var wanted = _dirty.TakeUpTo(ReadsPerBeat);
        var unloaded = new List<(int, int)>();
        foreach (var chunk in wanted)
        {
            switch (ColumnPump.TryRead(_api, chunk.Item1, chunk.Item2, _shows, _chiselled, _surface, out var record))
            {
                case Readiness.Unready:
                    // Loaded but not finished building. It waits for a later beat.
                    _dirty.Restore(new[] { chunk });
                    continue;
                case Readiness.Unloaded:
                    unloaded.Add(chunk);
                    continue;
            }

            _recent.Keep(chunk, record!);
            _touched.Drop(new[] { chunk });

            if (_known.TryGetValue(chunk, out var known) && known.Crc == Crc32.Of(record!))
            {
                // Any block moving marks a chunk dirty, and most of those moves
                // are underground where the surface does not change. Comparing
                // here keeps mining off the wire.
                Settled(new[] { chunk });
                continue;
            }

            entries.Add(Entry.Of(chunk, record!, SeasonOf(chunk, edge)));
            sent.Add(chunk);
        }

        // A chunk that cannot be read is forgotten here and added to what the
        // server must be asked for.
        _dirty.Forget(unloaded);
        _repair.Owe(unloaded);

        foreach (var (chunk, season) in _turned)
        {
            entries.Add(Entry.Season(chunk, season));
        }
        var turned = new Dictionary<(int, int), byte>(_turned);
        _turned.Clear();

        if (entries.Count == 0)
        {
            return;
        }

        // These are recorded as sent now rather than when the post lands,
        // because the next beat reads this to tell whether a chunk changed. The
        // failure callback below puts everything back.
        foreach (var entry in entries)
        {
            if (entry.Record is not null)
            {
                _known[entry.Chunk] = new Known(Crc32.Of(entry.Record), entry.SeasonByte);
            }
            else if (_known.TryGetValue(entry.Chunk, out var known))
            {
                _known[entry.Chunk] = known with { Season = entry.SeasonByte };
            }
        }
        Settled(sent);

        _service.Terrain(Json(edge, entries), failed: () =>
        {
            // Hop back to the game thread, because everything here lives there.
            // The ground returns to the pile to be read and sent again, and the
            // seasons return to be sent again.
            _api.Event.EnqueueMainThreadTask(() =>
            {
                foreach (var chunk in sent)
                {
                    _known.Remove(chunk);
                }
                _dirty.Restore(sent);
                foreach (var (chunk, season) in turned)
                {
                    _turned[chunk] = season;
                }
            }, "witchlight-terrain-restore");
        });
    }

    /// <summary>
    /// Runs one beat of the slow lane, on the game thread.
    ///
    /// This checks where the year has reached for every chunk the map holds, and
    /// queues a whole re-read of every chunk the fast lane answered by patching.
    /// It returns a line describing what happened, for the command that asks.
    /// </summary>
    public string Export(string reason, bool force = false)
    {
        KeepWorldFacts();

        if (force)
        {
            // An operator typing the command expects everything in memory to be
            // read again, whether or not the server thinks it moved.
            _dirty.MarkAll(LoadedColumns(_api));
            _repair.Ask(Repair.PerCommand);
        }

        // Read whole everything the server marked dirty since last time that the
        // fast lane did not read whole, in case no block event described the
        // change.
        var catchAll = _touched.Take();
        _dirty.Restore(catchAll);

        var edge = _api.WorldManager.ChunkSize;
        var seasonsNow = ColumnPump.Seasons(_api, _known.Keys, edge);
        var aged = 0;
        foreach (var (chunk, season) in seasonsNow)
        {
            if (_known[chunk].Season != season)
            {
                _turned[chunk] = season;
                aged++;
            }
        }

        var message = $"{catchAll.Count} chunks queued for a whole read"
            + (aged > 0 ? $", the season turned in {aged}" : "")
            + $", {_known.Count} chunks mapped, {_dirty.Count} waiting, on {reason}{Owing()}";
        if (force || aged > 0)
        {
            _api.Logger.Notification("[witchlight] {0}", message);
        }
        return message;
    }

    /// <summary>
    /// Asks the server for a few of the columns the map wants back. This runs on
    /// a clock of its own. See <see cref="Repair"/>.
    /// </summary>
    public void Fetch() => _repair.Ask(Repair.PerStep);

    /// <summary>
    /// Asks the service once what it already holds, and takes that as what has
    /// been sent.
    ///
    /// Until the answer lands everything counts as never sent. That costs one
    /// read and one post per chunk loaded in the meantime and nothing more,
    /// because the service stores nothing for a record it already has. The answer
    /// merges under what this side has learned since, because a chunk sent a
    /// moment ago is newer than the service's answer about it.
    /// </summary>
    private void Sync()
    {
        if (_syncing)
        {
            return;
        }
        _syncing = true;

        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            var body = await _service.Held().ConfigureAwait(false);
            _api.Event.EnqueueMainThreadTask(() =>
            {
                _syncing = false;
                if (body is null)
                {
                    return;
                }

                try
                {
                    var read = JObject.Parse(body);
                    var held = new List<(int, int)>();
                    foreach (var one in read["Chunks"] as JArray ?? new JArray())
                    {
                        var chunk = ((int)one[0]!, (int)one[1]!);
                        if (!_known.ContainsKey(chunk))
                        {
                            _known[chunk] = new Known((uint)one[2]!, (byte)one[3]!);
                        }
                        held.Add(chunk);
                    }
                    _dirty.Seed(held);
                    _touched.Seed(held);
                    _synced = true;
                    _api.Logger.Notification("[witchlight] the map service holds {0} chunks", held.Count);
                }
                catch (Exception error)
                {
                    _api.Logger.Warning("[witchlight] could not read what the map holds: {0}", error.Message);
                }
            }, "witchlight-terrain-sync");
        });
    }

    /// <summary>
    /// Records that these columns have reached the service. Reaching the service
    /// stops a column being re-read for merely loading again, and settles a
    /// column the map was owed.
    /// </summary>
    private void Settled(IReadOnlyCollection<(int, int)> columns)
    {
        _dirty.Exported(columns);
        _touched.Exported(columns);
        _repair.Settled(columns);
    }

    /// <summary>Returns the season to send a chunk with, read fresh from the calendar.</summary>
    private byte SeasonOf((int, int) chunk, int edge) =>
        ColumnPump.Seasons(_api, new[] { chunk }, edge)[chunk];

    /// <summary>Returns the line the status command prints about the terrain.</summary>
    public string Describe() =>
        $"terrain: {_known.Count} chunks on the map, {_recent.Count} held for quick reads, "
        + $"{_chiselled.Kinds} chiselled block(s) resolved to their material"
        + (_synced ? "" : ", not yet told what the map holds");

    /// <summary>Returns the clause an export prints about columns still owed, or an empty string.</summary>
    private string Owing() => _repair.Owed > 0 ? $", {_repair.Owed} columns still owed" : "";

    /// <summary>Returns every chunk column the server currently holds in memory.</summary>
    private static IEnumerable<(int, int)> LoadedColumns(ICoreServerAPI api)
    {
        foreach (var index in new List<long>(api.WorldManager.AllLoadedMapchunks.Keys))
        {
            var pos = api.WorldManager.MapChunkPosFromChunkIndex2D(index);
            yield return (pos.X, pos.Y);
        }
    }

    /// <summary>Carries one chunk on the wire, as ground that moved or a season that turned.</summary>
    private sealed record Entry((int, int) Chunk, byte[]? Record, byte SeasonByte)
    {
        public static Entry Of((int, int) chunk, byte[] record, byte season) =>
            new(chunk, (byte[])record.Clone(), season);

        public static Entry Season((int, int) chunk, byte season) => new(chunk, null, season);
    }

    /// <summary>
    /// Builds the post the service reads: the chunk edge, then one entry per
    /// chunk. A record travels deflated and base64 encoded.
    /// </summary>
    private static string Json(int edge, IReadOnlyList<Entry> entries)
    {
        var chunks = new List<object>(entries.Count);
        foreach (var entry in entries)
        {
            if (entry.Record is null)
            {
                chunks.Add(new { X = entry.Chunk.Item1, Z = entry.Chunk.Item2, Season = entry.SeasonByte });
            }
            else
            {
                chunks.Add(new
                {
                    X = entry.Chunk.Item1,
                    Z = entry.Chunk.Item2,
                    Season = entry.SeasonByte,
                    Record = Convert.ToBase64String(Packed(entry.Record)),
                });
            }
        }
        return JsonConvert.SerializeObject(new { Edge = edge, Chunks = chunks });
    }

    /// <summary>Deflates a record raw, the way the service inflates one.</summary>
    private static byte[] Packed(byte[] record)
    {
        using var packed = new MemoryStream();
        using (var packing = new DeflateStream(packed, CompressionLevel.Optimal, leaveOpen: true))
        {
            packing.Write(record, 0, record.Length);
        }
        return packed.ToArray();
    }

    /// <summary>
    /// Holds the records most recently read and evicts the least recently used
    /// first.
    /// </summary>
    private sealed class Recent
    {
        private readonly int _most;
        private readonly Dictionary<(int, int), LinkedListNode<((int, int) Chunk, byte[] Record)>> _held = new();
        private readonly LinkedList<((int, int) Chunk, byte[] Record)> _order = new();

        public Recent(int most) => _most = most;

        public int Count => _held.Count;

        public bool TryGet((int, int) chunk, out byte[] record)
        {
            if (_held.TryGetValue(chunk, out var node))
            {
                _order.Remove(node);
                _order.AddLast(node);
                record = node.Value.Record;
                return true;
            }
            record = Array.Empty<byte>();
            return false;
        }

        public void Keep((int, int) chunk, byte[] record)
        {
            if (_held.TryGetValue(chunk, out var node))
            {
                _order.Remove(node);
            }
            var kept = new LinkedListNode<((int, int), byte[])>((chunk, (byte[])record.Clone()));
            _order.AddLast(kept);
            _held[chunk] = kept;

            while (_held.Count > _most && _order.First is { } oldest)
            {
                _order.RemoveFirst();
                _held.Remove(oldest.Value.Chunk);
            }
        }
    }
}
