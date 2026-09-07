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
/// What another mod uses to keep rows on the map.
///
/// A plugin is a mod of its own. It declares what its rows look like, sends
/// them, and ships a script the map page runs — and it never speaks HTTP, never
/// holds the service's address or token, and never writes SQL. All three of
/// those live here, because all three are things that go wrong quietly when
/// every plugin has its own copy: the service's port moves every time it starts,
/// its token is minted fresh, and the recovery from both is dropping a cached
/// endpoint at exactly the right moment. <see cref="MapService"/> already does
/// that correctly and this is a way through to it.
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
/// Reference Witchlight at compile time only, with Copy Local off: two mod
/// assemblies carrying the same ModSystem break both. Declare it in
/// <c>modinfo.json</c> under <c>dependencies</c>, or guard with
/// <c>api.ModLoader.IsModEnabled("witchlight")</c> from a method of its own —
/// a local of a type from an absent mod fails at method entry, guard or no
/// guard, so the guard has to be in a different method from the use.
/// </summary>
public sealed class WitchlightPlugins
{
    /// <summary>
    /// Rows waiting to be sent, by plugin.
    ///
    /// Buffered rather than posted as they are made. A collector scanning ore
    /// produces rows far faster than a five-second HTTP round trip retires them,
    /// and posting each one would either block the game thread or, with the
    /// drop-if-busy rule the live feeds use, quietly lose most of them. A
    /// position dropped is replaced two seconds later; a row dropped is gone,
    /// so these queue and drain on a tick.
    /// </summary>
    private readonly ConcurrentDictionary<string, PendingRows> _waiting = new();

    /// <summary>Which plugins have registered, so a send can say if one has not.</summary>
    private readonly ConcurrentDictionary<string, byte> _registered = new();

    private readonly MapService _service;
    private readonly ILogger _log;

    /// <summary>Where the map keeps everything, which is where a plugin's own
    /// files are copied to.</summary>
    private readonly string _exports;

    /// <summary>Whether a drain is already running, so two ticks do not overlap.</summary>
    private int _draining;

    internal WitchlightPlugins(MapService service, ILogger log, string exports)
    {
        _service = service;
        _log = log;
        _exports = exports;
    }

    /// <summary>One row, whose it is, and what they are called.</summary>
    private sealed record Pending(string Owner, string Called, object Row);

    /// <summary>
    /// Rows waiting for one plugin, oldest first.
    ///
    /// A list behind a lock rather than a <see cref="ConcurrentQueue{T}"/>,
    /// because a batch that fails has to go back at the FRONT and a concurrent
    /// queue can only append. That ordering is not a nicety: a plugin that sends
    /// a row for a key, fails, and then sends a newer row for the same key would
    /// otherwise have the older one land last and overwrite the newer, since the
    /// service replaces a row by its key. Oldest-first in, oldest-first out, and
    /// a failure changes neither.
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
        /// Up to <paramref name="most"/> rows from the front, all of one owner,
        /// taken off. Empty where there is nothing waiting.
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

        /// <summary>A batch that did not land, back where it was taken from.</summary>
        public void PutBack(List<Pending> batch)
        {
            lock (_rows) { _rows.InsertRange(0, batch); }
        }
    }

    /// <summary>How many rows go in one post. Enough to be worth a round trip.</summary>
    private const int Batch = 500;

    /// <summary>How many times to ask the service to take a registration, and
    /// how long to leave between asks. Thirty seconds in total, which is far
    /// longer than a service takes to bind and short enough that a service that
    /// is never coming says so while somebody is still watching the log.</summary>
    private const int RegisterTries = 30;
    private const int RegisterWaitMs = 1000;

    /// <summary>
    /// Says what a plugin is, and what its rows look like.
    ///
    /// The plugin is named by its own modid, taken from the mod handed in. That
    /// is the one name it has: the map keys its store, its addresses and its
    /// sharing by it, and its files are served under it.
    ///
    /// Called once, from the plugin's own <c>Start</c>. Makes the plugin's
    /// database where there is none and carries an added column onto one that is
    /// already there; anything else that moved is refused and said so, with the
    /// rows left where they are.
    ///
    /// Answers a handle to keep, or null where the service would not take it —
    /// which is not fatal to the game and is not treated as such: a plugin whose
    /// rows are not being kept should say so in the log and go on running.
    /// </summary>
    public async Task<RegisteredPlugin?> Register(Mod mod, PluginShape shape)
    {
        // The mod's own id, rather than one it repeats. A plugin is addressed by
        // this everywhere — its store, its rows, the files it ships, who it is
        // shared with — and a second copy of the name is a second thing to keep
        // in step with the first.
        var id = mod.Info?.ModID ?? "";

        if (string.IsNullOrWhiteSpace(id) || shape.Columns.Count == 0 || shape.Key.Count == 0)
        {
            _log.Warning("[witchlight] plugin {0}: a shape needs columns and a key", id);
            return null;
        }

        // The plugin's own files, out of its mod and beside the map. Done here
        // rather than by the plugin so that installing one is installing a mod
        // and nothing else: nobody unpacks a second archive into a directory
        // they should not have to know exists.
        //
        // A plugin whose files will not copy still registers and still keeps
        // rows. It draws nothing, which is worth saying and is not worth
        // refusing to store somebody's data over.
        if (PluginFiles.Install(mod, id, _exports, _log) is { } wrong)
        {
            _log.Warning("[witchlight] plugin {0}: nothing to draw with — {1}", id, wrong);
        }

        // Waited for rather than tried once. The map service is a process this
        // mod has just started, and it writes the file naming its port a moment
        // after it is up — so the first ask lands before there is anything to
        // ask. Everything else this mod sends is on a clock and simply arrives
        // late; a registration happens once, and one that failed would leave a
        // plugin storing nothing until the server was restarted.
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
    /// One row, kept.
    ///
    /// Queued rather than sent, so this returns at once and may be called from
    /// wherever a plugin finds something worth keeping. What is queued goes out
    /// on the next drain.
    ///
    /// <paramref name="owner"/> is the player whose row this is; a world-scoped
    /// plugin passes null. The player rather than their uid, so that who a row
    /// belongs to and what they are called cannot be given as a mismatched pair —
    /// the name is what a reader is shown for a row shared by somebody who is not
    /// online. The service takes this as the answer about who may see the row,
    /// which is why it is a player the mod knows and not anything a page said.
    /// </summary>
    public void Store(string id, object row, IPlayer? owner = null) =>
        _waiting.GetOrAdd(id, _ => new PendingRows())
            .Add(new Pending(owner?.PlayerUID ?? "", owner?.PlayerName ?? "", row));

    /// <summary>Many rows at once, which is the same queue.</summary>
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
    /// Rows the service is holding, as this plugin may see them.
    ///
    /// <paramref name="ranges"/> names the columns to bound and by how much,
    /// which the plugin must have declared ranged. Everything, where none are
    /// given — which is fine for a small plugin and is not for one holding a
    /// world's worth of anything.
    /// </summary>
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

    /// <summary>
    /// How many rows are waiting to go out, for the status an operator reads.
    /// </summary>
    public int Waiting => _waiting.Values.Sum(rows => rows.Count);

    /// <summary>
    /// Sends what has piled up since the last one.
    ///
    /// Called on a tick rather than by a plugin. One drain at a time, and one
    /// post per plugin per drain, so a plugin with a backlog empties over several
    /// ticks instead of holding the queue for all of them.
    ///
    /// A batch that does not land is put back at the front — a row is worth
    /// keeping or it would not have been sent, and a service that is restarting
    /// is a reason to wait rather than to throw the rows away.
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
                    // the rows are. A batch stops at the first row of somebody
                    // else's, which keeps the common case — one player's finds —
                    // to one post.
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
                        // A warning rather than a debug line. Rows held back are
                        // rows the map does not have, which is the thing an
                        // operator is looking for when a plugin draws nothing —
                        // and a debug line is not on in the log they are reading.
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
/// One plugin's own way in, once it has registered.
///
/// The same three things <see cref="WitchlightPlugins"/> offers, with the
/// plugin's name already filled in. That is the whole of it: a plugin holding
/// one of these cannot name another plugin's store by mistake, and cannot spell
/// its own wrongly — the name came from its modid and it never types it.
/// </summary>
public sealed class RegisteredPlugin
{
    private readonly WitchlightPlugins _plugins;

    internal RegisteredPlugin(WitchlightPlugins plugins, string id)
    {
        _plugins = plugins;
        Id = id;
    }

    /// <summary>What this plugin is called, which is its modid.</summary>
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
