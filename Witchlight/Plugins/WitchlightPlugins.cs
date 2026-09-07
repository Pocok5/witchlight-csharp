using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Common;

namespace Witchlight;

/// <summary>
/// The API another mod uses to keep rows on the map.
///
/// A plugin is a mod of its own. It declares what its rows look like, sends them,
/// and ships a script the map page runs. It never speaks HTTP, never holds the
/// service's address or token, and never writes SQL. All three live here, because
/// all three go wrong quietly when every plugin has its own copy. The service's
/// port moves every time it starts and its token is minted fresh, and recovering
/// from both means dropping a cached endpoint at the right moment.
/// <see cref="MapService"/> does that, and this class is the way through to it.
///
/// Reached from a plugin's own <c>Start</c>:
///
/// <code>
/// var wl = api.ModLoader.GetModSystem&lt;WitchlightSystem&gt;();
/// await wl.Plugins.Register(Mod, new PluginShape()
///     .Column("x", PluginKind.Int).Column("y", PluginKind.Int)
///     .Column("z", PluginKind.Int).Column("code", PluginKind.Text)
///     .KeyedBy("x", "y", "z").RangedBy("x", "z")
///     .SeenBy(PluginScope.Owner));
/// </code>
///
/// Reference Witchlight at compile time only, with Copy Local off. Two mod
/// assemblies carrying the same ModSystem break both. Declare it in
/// <c>modinfo.json</c> under <c>dependencies</c>, or guard with
/// <c>api.ModLoader.IsModEnabled("witchlight")</c> from a method of its own. A
/// local of a type from an absent mod fails at method entry whether or not a
/// guard precedes it, so the guard must be in a different method from the use.
/// </summary>
public sealed class WitchlightPlugins
{
    /// <summary>
    /// Rows waiting to be sent, by plugin.
    ///
    /// Buffers rows rather than posting each as it is made. A collector scanning
    /// ore produces rows far faster than a five-second HTTP round trip retires
    /// them, so posting each one would either block the game thread or, under the
    /// drop-if-busy rule the live feeds use, lose most of them. A dropped
    /// position is replaced two seconds later, while a dropped row is gone.
    /// </summary>
    private readonly ConcurrentDictionary<string, PendingRows> _waiting = new();

    /// <summary>The plugins that have registered, so a send can report one that
    ///  has not.</summary>
    private readonly ConcurrentDictionary<string, byte> _registered = new();

    private readonly MapService _service;
    private readonly ILogger _log;

    /// <summary>The map's export directory, which is where a plugin's own files
    /// are copied to.</summary>
    private readonly string _exports;

    /// <summary>True while a drain is running, so two ticks do not overlap.</summary>
    private int _draining;

    internal WitchlightPlugins(MapService service, ILogger log, string exports)
    {
        _service = service;
        _log = log;
        _exports = exports;
    }

    /// <summary>One row, the uid of its owner, and that owner's name.</summary>
    private sealed record Pending(string Owner, string Called, object Row);

    /// <summary>
    /// Rows waiting for one plugin, oldest first.
    ///
    /// A list behind a lock rather than a <see cref="ConcurrentQueue{T}"/>,
    /// because a failed batch goes back at the front and a concurrent queue can
    /// only append. The service replaces a row by its key, so a plugin that sends
    /// a row, fails, and then sends a newer row for the same key would otherwise
    /// land the older one last and overwrite the newer.
    /// </summary>
    private sealed class PendingRows
    {
        private readonly List<Pending> _rows = new();

        public int Count
        {
            get { lock (_rows) { return _rows.Count; } }
        }

        public void Add(Pending row)
        {
            lock (_rows) { _rows.Add(row); }
        }

        /// <summary>
        /// Takes up to <paramref name="most"/> rows off the front, all belonging
        /// to one owner. Returns an empty batch when nothing is waiting.
        /// </summary>
        public List<Pending> Take(int most)
        {
            lock (_rows)
            {
                if (_rows.Count == 0)
                {
                    return new List<Pending>();
                }

                var owner = _rows[0].Owner;
                var taken = 0;
                while (taken < most && taken < _rows.Count && _rows[taken].Owner == owner)
                {
                    taken++;
                }

                var batch = _rows.GetRange(0, taken);
                _rows.RemoveRange(0, taken);
                return batch;
            }
        }

        /// <summary>Puts a batch that did not land back at the front of the queue.</summary>
        public void PutBack(List<Pending> batch)
        {
            lock (_rows) { _rows.InsertRange(0, batch); }
        }
    }

    /// <summary>How many rows go in one post. Enough to be worth a round trip.</summary>
    private const int Batch = 500;

    /// <summary>How many times to offer a registration, and how long to wait
    /// between attempts. Thirty seconds in total, far longer than a service takes
    /// to bind and short enough to report a service that is never coming while
    /// somebody is still watching the log.</summary>
    private const int RegisterTries = 30;
    private const int RegisterWaitMs = 1000;

    /// <summary>
    /// Registers a plugin and the shape of its rows. Returns a handle to keep, or
    /// null when the service would not take it.
    ///
    /// Names the plugin by the modid of the mod handed in. The map keys its store,
    /// its addresses and its sharing by that name, and serves its files under it.
    ///
    /// Call once, from the plugin's own <c>Start</c>. Creates the plugin's table
    /// where there is none and carries an added column onto one already there.
    /// Refuses any other change and reports it, leaving the rows where they are.
    ///
    /// A null return is not fatal. A plugin whose rows are not being kept should
    /// log that and go on running.
    /// </summary>
    public async Task<RegisteredPlugin?> Register(Mod mod, PluginShape shape)
    {
        // Take the mod's own id rather than one the plugin repeats. This name
        // addresses the plugin's store, its rows, the files it ships and who it
        // is shared with, so a second copy would be a second thing to keep in
        // step.
        var id = mod.Info?.ModID ?? "";

        if (string.IsNullOrWhiteSpace(id) || shape.Columns.Count == 0 || shape.Key.Count == 0)
        {
            _log.Warning("[witchlight] plugin {0}: a shape needs columns and a key", id);
            return null;
        }

        // Copy the plugin's files here rather than in the plugin, so installing
        // a plugin is installing a mod and nothing else.
        //
        // A plugin whose files will not copy still registers and still keeps
        // rows. It draws nothing, which is worth logging and is not worth
        // refusing to store somebody's data over.
        if (PluginFiles.Install(mod, id, _exports, _log) is { } wrong)
        {
            _log.Warning("[witchlight] plugin {0}: nothing to draw with — {1}", id, wrong);
        }

        // Retry rather than trying once. The mod has just started the map
        // service, which writes the file naming its port a moment after it is up,
        // so the first attempt lands before there is anything to ask. Everything
        // else this mod sends is on a clock and arrives late instead, while a
        // registration happens once and a failed one would leave the plugin
        // storing nothing until the next server restart.
        var body = JsonConvert.SerializeObject(shape);
        string? said = null;
        for (var attempt = 0; attempt < RegisterTries; attempt++)
        {
            said = await _service.PluginRegister(id, body).ConfigureAwait(false);
            if (said is null)
            {
                break;
            }
            await Task.Delay(RegisterWaitMs).ConfigureAwait(false);
        }

        if (said is not null)
        {
            _log.Warning(
                "[witchlight] plugin {0} was refused after {1}s: {2}",
                id,
                RegisterTries * RegisterWaitMs / 1000,
                said);
            return null;
        }

        _registered[id] = 0;
        _log.Notification("[witchlight] plugin {0}: registered", id);
        return new RegisteredPlugin(this, id);
    }

    /// <summary>
    /// Stores one row.
    ///
    /// Queues the row and returns at once, so a plugin may call this wherever it
    /// finds something worth keeping. The queue goes out on the next drain.
    /// </summary>
    /// <param name="owner">
    /// The player the row belongs to. A world-scoped plugin passes null. Takes
    /// the player rather than their uid, so the uid and the name cannot be given
    /// as a mismatched pair. A reader is shown that name for a row shared by
    /// somebody who is not online, and the service decides who may see the row
    /// from it, so it must be a player the mod knows rather than anything a page
    /// said.
    /// </param>
    public void Store(string id, object row, IPlayer? owner = null) =>
        _waiting.GetOrAdd(id, _ => new PendingRows())
            .Add(new Pending(owner?.PlayerUID ?? "", owner?.PlayerName ?? "", row));

    /// <summary>Stores many rows at once, onto the same queue.</summary>
    public void StoreMany(string id, IEnumerable<object> rows, IPlayer? owner = null)
    {
        var queue = _waiting.GetOrAdd(id, _ => new PendingRows());
        var uid = owner?.PlayerUID ?? "";
        var called = owner?.PlayerName ?? "";
        foreach (var row in rows)
        {
            queue.Add(new Pending(uid, called, row));
        }
    }

    /// <summary>
    /// Returns the rows the service is holding for this plugin.
    /// </summary>
    /// <param name="ranges">
    /// The columns to bound and by how much. The plugin must have declared each
    /// one ranged. Passing none returns everything, which suits a small plugin
    /// and not one holding a world's worth of rows.
    /// </param>
    public async Task<JArray> Query(string id, IReadOnlyDictionary<string, (long Low, long High)>? ranges = null)
    {
        var query = ranges is null || ranges.Count == 0
            ? ""
            : "?" + string.Join("&", ranges.Select(r => $"{r.Key}={r.Value.Low}..{r.Value.High}"));

        var said = await _service.PluginQuery(id, query).ConfigureAwait(false);
        if (said is null)
        {
            return new JArray();
        }

        try
        {
            return JArray.Parse(said);
        }
        catch (JsonException error)
        {
            _log.Warning("[witchlight] plugin {0}: could not read what came back: {1}", id, error.Message);
            return new JArray();
        }
    }

    /// <summary>Returns how many rows are waiting to go out, for the status line.</summary>
    public int Waiting => _waiting.Values.Sum(rows => rows.Count);

    /// <summary>
    /// Sends the rows that piled up since the last drain.
    ///
    /// Runs on a tick rather than being called by a plugin. Runs one drain at a
    /// time and posts once per plugin per drain, so a plugin with a backlog
    /// empties over several ticks instead of holding the queue for all of them.
    ///
    /// Puts a batch that does not land back at the front. A restarting service is
    /// a reason to wait rather than to throw the rows away.
    /// </summary>
    internal void Drain()
    {
        if (Interlocked.CompareExchange(ref _draining, 1, 0) != 0)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                foreach (var (id, waiting) in _waiting)
                {
                    if (!_registered.ContainsKey(id))
                    {
                        continue;
                    }

                    // One owner per post, since the service is told once whose
                    // the rows are. A batch stops at the first row belonging to
                    // somebody else, which keeps one player's finds to one post.
                    var batch = waiting.Take(Batch);
                    if (batch.Count == 0)
                    {
                        continue;
                    }

                    var owner = batch[0].Owner;
                    var body = JsonConvert.SerializeObject(new
                    {
                        Owner = owner,
                        OwnerName = batch[0].Called,
                        Rows = batch.Select(pending => pending.Row).ToList(),
                    });

                    var said = await _service.PluginStore(id, body).ConfigureAwait(false);
                    if (said is not null)
                    {
                        waiting.PutBack(batch);
                        // Warn rather than log at debug. Rows held back are rows
                        // the map does not have, which is what an operator looks
                        // for when a plugin draws nothing, and debug logging is
                        // off in the log they are reading.
                        _log.Warning(
                            "[witchlight] plugin {0}: {1} rows held back, {2}", id, batch.Count, said);
                    }
                    else
                    {
                        _log.Notification(
                            "[witchlight] plugin {0}: {1} rows sent.", id, batch.Count);
                    }
                }
            }
            catch (Exception error)
            {
                _log.Warning("[witchlight] plugin rows could not be sent: {0}", error.Message);
            }
            finally
            {
                Interlocked.Exchange(ref _draining, 0);
            }
        });
    }
}

/// <summary>
/// One plugin's handle, returned once it has registered.
///
/// Offers the same three calls <see cref="WitchlightPlugins"/> does, with the
/// plugin's name already filled in. A plugin holding one of these cannot name
/// another plugin's store or misspell its own, since the name came from its modid
/// and the plugin never types it.
/// </summary>
public sealed class RegisteredPlugin
{
    private readonly WitchlightPlugins _plugins;

    internal RegisteredPlugin(WitchlightPlugins plugins, string id)
    {
        _plugins = plugins;
        Id = id;
    }

    /// <summary>This plugin's name, which is its modid.</summary>
    public string Id { get; }

    /// <inheritdoc cref="WitchlightPlugins.Store"/>
    public void Store(object row, IPlayer? owner = null) => _plugins.Store(Id, row, owner);

    /// <inheritdoc cref="WitchlightPlugins.StoreMany"/>
    public void StoreMany(IEnumerable<object> rows, IPlayer? owner = null) =>
        _plugins.StoreMany(Id, rows, owner);

    /// <inheritdoc cref="WitchlightPlugins.Query"/>
    public Task<JArray> Query(IReadOnlyDictionary<string, (long Low, long High)>? ranges = null) =>
        _plugins.Query(Id, ranges);
}
