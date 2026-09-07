using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Common;

namespace Witchlight;

/// <summary>
/// Posts everything that moves to the map service over HTTP.
///
/// Player positions are worth nothing once they are old, so they go over a socket
/// rather than through a file the service reads back.
///
/// The service listens on loopback on whatever port the machine had free, and
/// writes that port and a token into `api.json` beside the map. Nothing off the
/// machine can reach loopback, and nothing on it can post without having read
/// that file.
///
/// The port changes every time the service starts, so this class reads the file
/// again whenever a post fails. That also covers the first moments after the mod
/// starts the service, when there is no file to read yet.
///
/// Set `WITCHLIGHT_API_BIND` and `WITCHLIGHT_API_TOKEN` to reach a service on
/// another machine, which no file beside this one can name, and set `api_bind`
/// and `api_token` to match on the service.
///
/// Nothing here blocks the game. Posts run on the thread pool, and a post is
/// dropped rather than queued while the last is still in flight. A position that
/// arrives late is worse than one skipped, since another follows two seconds
/// behind.
/// </summary>
public sealed class MapService : IDisposable
{
    private const string BindVariable = "WITCHLIGHT_API_BIND";
    private const string TokenVariable = "WITCHLIGHT_API_TOKEN";

    /// <summary>The address the service published, and the token it wants.</summary>
    private sealed record Endpoint(string Url, string Token);

    private readonly HttpClient _client;
    private readonly ILogger _log;
    private readonly string _exports;

    /// <summary>
    /// The endpoint last read out of `api.json`, or null when there was none.
    /// Replaced wholesale rather than mutated, so a post already in flight
    /// finishes against the endpoint it started with.
    /// </summary>
    private volatile Endpoint? _endpoint;

    /// <summary>
    /// One kind of thing this posts, with its own path, health and in-flight
    /// flag.
    ///
    /// Each feed carries its own health because they post at different rates.
    /// Players go every two seconds and markers every fifteen, so one shared
    /// health field would let a succeeding player post hide a failing marker post
    /// and report healthy while half the data went nowhere.
    /// </summary>
    private sealed class Feed(string name, string path)
    {
        private int _sending;

        /// <summary>The feed's name, for the log and the status line.</summary>
        public string Name { get; } = name;

        /// <summary>The path the feed is posted to.</summary>
        public string Path { get; } = path;

        /// <summary>What happened to the last post on this feed.</summary>
        public string Health { get; set; } = "nothing sent yet";

        /// <summary>
        /// Claims the right to post on this feed. Returns false when a post is
        /// already in flight.
        ///
        /// One post at a time. A position that arrives late is worse than one
        /// skipped, since another follows two seconds behind.
        /// </summary>
        public bool Claim() => Interlocked.CompareExchange(ref _sending, 1, 0) == 0;

        /// <summary>True while a post on this feed is in flight.</summary>
        public bool Busy => Volatile.Read(ref _sending) != 0;

        /// <summary>Releases the feed so the next post may start.</summary>
        public void Release() => Interlocked.Exchange(ref _sending, 0);
    }

    private readonly Feed _players = new("players", "/live/players");
    private readonly Feed _markers = new("markers", "/live/markers");
    private readonly Feed _claims = new("claims", "/live/claims");
    private readonly Feed _world = new("world", "/live/world");
    private readonly Feed _terrain = new("terrain", "/terrain");

    public string PlayersHealth => HealthOf(_players);

    public string MarkersHealth => HealthOf(_markers);

    public string ClaimsHealth => HealthOf(_claims);

    public string WorldHealth => HealthOf(_world);

    public string TerrainHealth => HealthOf(_terrain);

    /// <summary>
    /// True while a terrain post is in flight.
    ///
    /// The exporter reads this before taking chunks off its pile. Terrain is the
    /// one feed where a dropped post loses ground rather than skipping a
    /// position, so nothing is taken until there is room to send it.
    /// </summary>
    public bool TerrainBusy => _terrain.Busy;

    /// <summary>Returns what one feed's last post did.</summary>
    private string HealthOf(Feed feed)
    {
        lock (_reporting)
        {
            return feed.Health;
        }
    }

    /// <summary>
    /// Guards each feed's health and the flag saying the fault has been logged.
    ///
    /// Every feed posts on its own threadpool thread and `/witchlight status`
    /// reads the health lines from the game thread, so this state has several
    /// writers and a reader that is none of them. One lock covers all the feeds,
    /// because logging the fault once is a claim about all of them together: two
    /// feeds failing in the same second is one outage.
    /// </summary>
    private readonly object _reporting = new();

    private int _collecting;
    private readonly TimeSpan _resendMarkers;
    private string _sentMarkers = "";
    private DateTime _markersSentAt = DateTime.MinValue;
    private string _sentClaims = "";
    private DateTime _claimsSentAt = DateTime.MinValue;
    private bool _complained;

    /// <param name="resendMarkersEvery">
    /// How long an unchanged marker list may go unsent. Injectable so a test can
    /// watch the resend without waiting minutes for it.
    /// </param>
    public MapService(string exports, ILogger log, TimeSpan? resendMarkersEvery = null)
    {
        _log = log;
        _exports = exports;
        _resendMarkers = resendMarkersEvery ?? TimeSpan.FromMinutes(5);

        // Set no BaseAddress. The port moves with every service start and a
        // client carries its base for life, so each post names its own address.
        _client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };

        _endpoint = Resolve();
        log.Notification(
            "[witchlight] posting live data to {0}",
            _endpoint?.Url ?? $"whatever {ConnectionPath(exports)} names, once the service has written it");
    }

    /// <summary>Returns the path of the connection file the service writes as it binds.</summary>
    public static string ConnectionPath(string exports) => Path.Combine(exports, "api.json");

    /// <summary>
    /// Returns the endpoint to post to, reading the connection file again when
    /// the last look found nothing.
    /// </summary>
    private Endpoint? Where() => _endpoint ??= Resolve();

    /// <summary>
    /// Reads the endpoint from the environment when an operator set one, and
    /// otherwise from the file the service wrote. Returns null when there is
    /// nothing to read.
    ///
    /// Returns null rather than guessing. A service that has not started yet and
    /// one that never will look the same from here, and the next tick tries
    /// again either way.
    /// </summary>
    private Endpoint? Resolve()
    {
        var bind = Environment.GetEnvironmentVariable(BindVariable) ?? "";
        var token = Environment.GetEnvironmentVariable(TokenVariable) ?? "";
        if (bind.Length > 0)
        {
            return new Endpoint($"http://{bind}", token);
        }

        try
        {
            var path = ConnectionPath(_exports);
            if (!File.Exists(path))
            {
                return null;
            }

            var read = JObject.Parse(File.ReadAllText(path));
            var port = (int?)read["Port"] ?? 0;
            var word = (string?)read["Token"] ?? "";
            if (port <= 0 || word.Length == 0)
            {
                return null;
            }

            return new Endpoint($"http://127.0.0.1:{port}", word);
        }
        catch (Exception error)
        {
            // Treat a half-written or unreadable file as no file. Something else
            // is wrong, and logging it every two seconds would not help.
            _log.Debug("[witchlight] could not read the connection file: {0}", error.Message);
            return null;
        }
    }

    /// <summary>
    /// Posts on the API channel and returns the deserialized reply, or null when
    /// there is nothing to return.
    ///
    /// Serves the three requests that expect an answer: minting a login word,
    /// reading what one player has set on the map, and keeping one preset. Each
    /// runs when somebody types or presses something rather than on the tick, and
    /// each caller awaits it off the game thread.
    ///
    /// Returns null when the service is not answering, and drops the stale
    /// address, so the next request reads where the next service bound.
    /// </summary>
    private async Task<string?> Ask(string path, object? body = null, bool quietly = false)
    {
        var endpoint = Where();
        if (endpoint is null)
        {
            return null;
        }

        try
        {
            using var reply = await Reach(
                endpoint, path, body is null ? null : JsonConvert.SerializeObject(body))
                .ConfigureAwait(false);
            return reply.IsSuccessStatusCode
                ? await reply.Content.ReadAsStringAsync().ConfigureAwait(false)
                : null;
        }
        catch (Exception error)
        {
            // Drop the address with it, so the next request reads where the next
            // service bound.
            _endpoint = null;
            var said = "[witchlight] the map did not answer {0}: {1}";
            if (quietly)
            {
                _log.Debug(said, path, error.Message);
            }
            else
            {
                _log.Warning(said, path, error.Message);
            }
            return null;
        }
    }

    /// <summary>
    /// Sends one POST to the service with its bearer token, and returns the
    /// response.
    ///
    /// The one place every post goes through. A refused token means the service
    /// restarted and minted a new one, which this treats the same as a refused
    /// connection.
    ///
    /// Takes a body already written as JSON, or null. This is the wire and
    /// nothing above it, so a body is composed before it arrives here. Collecting
    /// markers asks for what the service holds and sends no body.
    /// </summary>
    private async Task<HttpResponseMessage> Reach(Endpoint endpoint, string path, string? json)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, endpoint.Url + path);
        if (json is not null)
        {
            message.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", endpoint.Token);

        var reply = await _client.SendAsync(message).ConfigureAwait(false);
        if (reply.StatusCode == HttpStatusCode.Unauthorized)
        {
            _endpoint = null;
        }
        return reply;
    }

    /// <summary>
    /// Registers one plugin with the service. Returns null when the service took
    /// it, and the complaint when it did not.
    ///
    /// <see cref="WitchlightPlugins"/> posts through this class rather than
    /// holding its own copy of the address and token.
    /// </summary>
    public Task<string?> PluginRegister(string id, string shape) =>
        Told($"/plugins/register/{id}", shape);

    /// <summary>Posts rows from a plugin's collector. Returns null when they landed.</summary>
    public Task<string?> PluginStore(string id, string body) =>
        Told($"/plugins/data/{id}", body);

    /// <summary>
    /// Returns what the service holds for one plugin, as JSON, or null when it
    /// could not be asked.
    ///
    /// Reads the map's own port rather than the API channel, because a browser
    /// asks the same question there and gets the same answer. Sends no session,
    /// so an owner-scoped plugin reads an empty list. A plugin reads its own rows
    /// back to migrate them.
    /// </summary>
    public async Task<string?> PluginQuery(string id, string query)
    {
        var endpoint = Where();
        if (endpoint is null)
        {
            return null;
        }

        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Get, $"{endpoint.Url}/data/{id}{query}");
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", endpoint.Token);
            using var reply = await _client.SendAsync(message).ConfigureAwait(false);
            if (reply.StatusCode == HttpStatusCode.Unauthorized)
            {
                _endpoint = null;
            }

            return reply.IsSuccessStatusCode
                ? await reply.Content.ReadAsStringAsync().ConfigureAwait(false)
                : null;
        }
        catch (Exception error)
        {
            _endpoint = null;
            _log.Debug("[witchlight] plugin {0} could not be asked: {1}", id, error.Message);
            return null;
        }
    }

    /// <summary>
    /// Posts JSON that is already written. Returns the complaint when the service
    /// refused it, and null when it landed.
    /// </summary>
    private async Task<string?> Told(string path, string json)
    {
        var endpoint = Where();
        if (endpoint is null)
        {
            return $"no service yet at {ConnectionPath(_exports)}";
        }

        try
        {
            using var reply = await Reach(endpoint, path, json).ConfigureAwait(false);
            if (reply.IsSuccessStatusCode)
            {
                return null;
            }

            var said = await reply.Content.ReadAsStringAsync().ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(said) ? $"the map answered {(int)reply.StatusCode}" : said;
        }
        catch (Exception error)
        {
            _endpoint = null;
            return error.Message;
        }
    }

    /// <summary>
    /// Asks the service to mint a login token for one player, and returns the
    /// whole address to hand them. Returns null when there is none to give.
    /// </summary>
    public async Task<string?> Link(string uid, string name, string where)
    {
        var body = await Ask("/auth/mint", new { Uid = uid, Name = name }).ConfigureAwait(false);
        if (body is null)
        {
            return null;
        }

        try
        {
            var word = (string?)JObject.Parse(body)["Token"];
            return string.IsNullOrEmpty(word) ? null : $"{where.TrimEnd('/')}/login?t={word}";
        }
        catch (Exception error)
        {
            _log.Warning("[witchlight] the map's login word could not be read: {0}", error.Message);
            return null;
        }
    }

    /// <summary>
    /// Returns what one player has set for themselves on the map: their presets,
    /// and where a new marker of theirs starts.
    ///
    /// The web form reads this over the public port under a session cookie. A
    /// game client has no cookie, so the mod asks on its behalf. The mod is the
    /// only party that knows which uid is which player, which is the same trust
    /// minting a login token already needs.
    ///
    /// Asks at the moment somebody marks something rather than caching. A preset
    /// made in a browser a minute ago has to apply to the next press of the key.
    /// </summary>
    public Task<string?> Presets(string uid) => Ask("/presets/of", new { Uid = uid });

    /// <summary>
    /// Saves one preset for one player, and returns everything they have set.
    ///
    /// Sends one preset rather than the whole document. The mod knows only the
    /// preset made in game, so writing a whole document back would delete every
    /// preset the player made in a browser.
    /// </summary>
    public Task<string?> KeepPreset(string uid, object preset) =>
        Ask("/presets/keep", new { Uid = uid, Preset = preset });

    /// <summary>
    /// Collects the markers and land claims players asked for on the web, and
    /// leaves the service holding none.
    ///
    /// The mod posts and the service answers, so the service cannot push a marker
    /// typed into the web form at the game. It queues it to be collected instead.
    /// Collecting empties the queue, so a reply that never reaches the caller
    /// loses what was in it. That is the right trade for a marker, since asking
    /// again is one more form while holding one twice puts two markers on the map.
    ///
    /// Returns null when there is nothing to collect or the service is not
    /// answering. Runs one request at a time, because the tick that asks is
    /// faster than a round trip on a busy server.
    /// </summary>
    public async Task<string?> Pending()
    {
        if (Interlocked.CompareExchange(ref _collecting, 1, 0) != 0)
        {
            return null;
        }

        try
        {
            // Fail quietly. This rides the two-second tick, and a service that
            // is down would otherwise log thirty times a minute. The other three
            // requests run because somebody pressed something and is owed the
            // reason out loud.
            return await Ask("/pending", quietly: true).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Exchange(ref _collecting, 0);
        }
    }

    /// <summary>Posts who is online and where. The service holds it in memory.</summary>
    public void Players(string json) => Post(_players, json);

    /// <summary>
    /// Posts every land claim, with the uids of who may be shown them.
    ///
    /// Sends only when the feed differs from what was sent last. Claims change a
    /// few times a week and make up the bulk of what there is to send. There is
    /// no slow resend, because the service keeps no claims on disk and a service
    /// that restarts holds none at all.
    /// </summary>
    public void Claims(string json)
    {
        var overdue = DateTime.UtcNow - _claimsSentAt >= _resendMarkers;
        if (json == _sentClaims && !overdue)
        {
            return;
        }

        Post(_claims, json);
    }

    /// <summary>Posts the world's clock, on its way to whoever is looking.</summary>
    public void World(string json) => Post(_world, json);

    /// <summary>
    /// Posts the ground: chunks whose surface moved, as records, and chunks whose
    /// season turned. The service writes them to its database and sends them to
    /// every browser. See the service's `apiport.rs`.
    /// </summary>
    /// <param name="failed">
    /// Called from the posting thread when the post did not land. The exporter
    /// puts the chunks back to be sent again, because a position can be skipped
    /// but the ground cannot.
    /// </param>
    public void Terrain(string json, Action failed) => Post(_terrain, json, failed);

    /// <summary>
    /// Returns the checksum and season of every chunk the service already holds,
    /// so an exporter that has just started does not read and send a chunk again
    /// for nothing. Returns null when the service is not answering yet, which is
    /// ordinary while the mod is still starting it.
    /// </summary>
    public Task<string?> Held() => Ask("/terrain/held", quietly: true);

    /// <summary>
    /// Posts every marker, when the feed differs from what was sent last.
    ///
    /// Markers change a few times an hour and make up the bulk of what there is
    /// to send, so posting them on the position timer would resend tens of
    /// kilobytes over and over.
    /// </summary>
    public void Markers(string json)
    {
        // Resend on a slow timer whether or not the list changed. A service that
        // restarted and lost its markers is still offered the same unchanged
        // list, and skipping it would leave the map without markers until
        // somebody moved one. The resend heals that within one interval.
        var overdue = DateTime.UtcNow - _markersSentAt >= _resendMarkers;
        if (json == _sentMarkers && !overdue)
        {
            return;
        }

        // Record the send when it lands, not when it is attempted. A post
        // dropped because the last is still in flight is not a send.
        Post(_markers, json);
    }

    private void Post(Feed feed, string json, Action? failed = null)
    {
        if (!feed.Claim())
        {
            failed?.Invoke();
            return;
        }

        _ = Task.Run(async () =>
        {
            // Read the connection file again when the last attempt found
            // nothing. The service the mod started may not have finished
            // binding.
            var endpoint = Where();
            if (endpoint is null)
            {
                Complain(feed, $"no service yet at {ConnectionPath(_exports)}");
                feed.Release();
                failed?.Invoke();
                return;
            }

            var landed = false;
            try
            {
                landed = await Send(feed, endpoint, json).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                // A service that is down is ordinary, since it is a separate
                // program the game does not depend on. Drop its address so the
                // next tick reads where the next one bound.
                _endpoint = null;
                Complain(feed, $"could not reach {endpoint.Url}: {error.Message}");
            }
            finally
            {
                feed.Release();
            }

            if (!landed)
            {
                failed?.Invoke();
            }
        });
    }

    private async Task<bool> Send(Feed feed, Endpoint endpoint, string json)
    {
        using var reply = await Reach(endpoint, feed.Path, json).ConfigureAwait(false);
        if (!reply.IsSuccessStatusCode)
        {
            Complain(feed, $"refused with {(int)reply.StatusCode}");
            return false;
        }

        // Record the send when it lands, not when it is attempted, so a post
        // dropped because the last was still in flight is not a send.
        if (feed == _markers)
        {
            _sentMarkers = json;
            _markersSentAt = DateTime.UtcNow;
        }
        else if (feed == _claims)
        {
            _sentClaims = json;
            _claimsSentAt = DateTime.UtcNow;
        }

        Recovered(feed, $"reaching {endpoint.Url}, {json.Length} bytes accepted");
        return true;
    }

    /// <summary>Records that a post landed, and logs the recovery only for the
    ///  first feed back.</summary>
    private void Recovered(Feed feed, string what)
    {
        lock (_reporting)
        {
            feed.Health = what;
            if (!_complained)
            {
                return;
            }

            _complained = false;
        }

        // Log outside the lock. The decision is what has to be atomic, and a
        // logger does I/O this must not hold a lock across.
        _log.Notification("[witchlight] the map service is taking live data again");
    }

    /// <summary>Records that a post failed, and logs the fault only once.</summary>
    private void Complain(Feed feed, string what)
    {
        lock (_reporting)
        {
            feed.Health = what;
            if (_complained)
            {
                return;
            }

            _complained = true;
        }

        _log.Warning(
            "[witchlight] {0}: {1} — that half will not show on the map until it is back",
            feed.Name,
            what);
    }

    public void Dispose()
    {
        _client.Dispose();
    }
}
