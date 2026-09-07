using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace Witchlight;

/// <summary>
/// Negotiates a usable block colour palette with connected clients.
///
/// A dedicated server's install ships 46 block texture files against a full
/// game's 9,587, so the palette it builds for itself colours nearly nothing. A
/// connected client has every texture and the block ids the server assigned, so
/// the palette it builds lines up with that server's exports.
///
/// The server builds what it can at asset load, keeps whatever a previous run or
/// a client left that still matches its registry, and asks a player for the rest.
/// This class decides when to ask, whom to ask, and what to do with an answer.
///
/// Anybody may fill in what is missing, and only an admin may replace what is
/// there. The two carry different risk. A colour laid over a block that has none
/// can only improve on nothing, and one laid over a colour somebody chose changes
/// what is already right. So a player's palette merges as filler, an admin's is
/// preferred, and `/witchlight palette` is the way back either way. Restricting
/// it to admins alone left a server whose operator never joins in game with no
/// map at all.
///
/// A palette is asked for until a client has supplied one for this mod set, and
/// then not again. Having nothing stored from a client for this block registry,
/// or a mod set that moved since it was stored, is what makes a client's palette
/// worth asking for on the server's own initiative. Once one has answered, the
/// map keeps that client's colours until a mod changes or an admin runs
/// `/witchlight palette`. Coverage and named gaps are reported and are not a
/// reason to ask. A block no client can colour is a block whose mod ships no
/// texture, and asking round the room does not change that.
/// </summary>
public sealed class PaletteExchange
{
    /// <summary>Sets how long to wait before reporting an ask as unanswered.</summary>
    private const int ReplyWaitMs = 30000;

    /// <summary>Sets how long one ask stands before another player may be asked.</summary>
    private static readonly TimeSpan AskAgainAfter = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Carries why somebody is being asked, in the two forms the log needs.
    ///
    /// The two lines describe the same packet and not the same event. A silence
    /// after one leaves a map with holes in it, and a silence after the other
    /// costs nothing. Reporting both the same way teaches an operator to skip the
    /// line that matters.
    /// </summary>
    private sealed record Reason(string Asked, string Missed);

    /// <summary>The reason used when the map cannot draw something a client's assets could.</summary>
    private static readonly Reason ForColours = new(
        "for a block colour palette",
        "so the map still has no colour for the blocks it is missing");

    /// <summary>
    /// Carries what the bootstrap settled on from one lifecycle stage to the next.
    ///
    /// The palette must be built at asset load and can only be asked for once the
    /// server is up. Those are two calls on two different APIs, so the build hands
    /// its answer over rather than leaving it in a field.
    /// </summary>
    public sealed record Built(Palette Palette, string Fingerprint, bool Needed);

    private readonly ICoreServerAPI _api;
    private readonly string _exports;

    /// <summary>Holds palette slices in flight, keyed by the player sending them.</summary>
    private readonly Dictionary<string, List<PaletteTable>> _incoming = new(StringComparer.Ordinal);

    /// <summary>
    /// Sets the most slices one palette can be on this server.
    ///
    /// A slice carries a fixed number of blocks and this server has only so many,
    /// so anything past this is not a palette. The sender need not be an admin, so
    /// without this bound a client claiming a million parts and streaming them
    /// would grow this process until it died.
    /// </summary>
    private readonly int _mostSlices;

    private string? _askedUid;
    private DateTime _askedAt = DateTime.MinValue;

    /// <summary>
    /// Tracks whether a client has yet to supply a palette for this mod set.
    ///
    /// This is set once at start from what is on disk, and cleared by the first
    /// whole palette a client sends. See <see cref="NeedsAClient"/>. Nothing else
    /// moves it, and it alone decides whether the server asks unprompted.
    /// </summary>
    private bool _needed;

    /// <summary>Holds the block ids the palette in hand says draw nothing.</summary>
    private HashSet<int> _hidden = new();

    /// <summary>
    /// Lists the blocks the palette in hand should be able to draw and cannot.
    ///
    /// This matters on a server whose palette is otherwise fine. A map may be 98%
    /// coloured and still have no colour for bare soil, which a player uncovers
    /// every time they dig. Coverage is one number over fourteen thousand blocks
    /// and cannot show that, so the gaps themselves are watched.
    /// </summary>
    private IReadOnlyList<string> _gaps = Array.Empty<string>();

    /// <summary>
    /// Holds who has been asked for a palette this run.
    ///
    /// Each player is asked once, because one ask is all a player has to give. A
    /// client sends the whole of what its assets can colour and the merge takes
    /// everything it could fill. Somebody who joins later has not been asked and
    /// may have the mod set that answers.
    ///
    /// Only `/witchlight palette` empties this, which is an admin saying the
    /// colours have moved under the same blocks. Nothing here can detect that
    /// case. This set also gates which palettes are accepted: one from a player
    /// not in here is refused.
    /// </summary>
    private readonly HashSet<string> _asked = new(StringComparer.Ordinal);

    public PaletteExchange(ICoreServerAPI api, string exports, Built built)
    {
        _api = api;
        _exports = exports;
        Fingerprint = built.Fingerprint;
        _mostSlices = built.Palette.Blocks.Count / PaletteTable.SliceSize + 2;
        _needed = built.Needed;
        Settled(built.Palette);
    }

    /// <summary>
    /// Reports whether a client's palette is worth asking for on the server's own
    /// initiative. That holds where nothing is stored from a client for this
    /// block registry, or the mod set moved since it was stored.
    ///
    /// This lives in one function because it is asked twice, once for the boot
    /// report and once to decide whether to ask, and two readings must agree.
    /// </summary>
    private static bool NeedsAClient(Palette palette, string fingerprint, bool moved) =>
        moved || palette.Source != "client" || palette.Fingerprint != fingerprint;

    /// <summary>Returns what this server's block registry hashes to.</summary>
    public string Fingerprint { get; }

    /// <summary>Reports whether the next player to join is asked for a palette.</summary>
    public bool Needed => _needed;

    /// <summary>Lists what this palette cannot draw and should be able to.</summary>
    public IReadOnlyList<string> Gaps => _gaps;

    /// <summary>
    /// Adopts the palette now in hand and recomputes the gaps and hidden blocks
    /// read from it.
    /// </summary>
    private void Settled(Palette palette)
    {
        _gaps = palette.Uncoloured;
        _hidden = palette.Blocks.Values
            .Where(entry => entry.Invisible == true)
            .Select(entry => entry.Id)
            .ToHashSet();
    }

    /// <summary>
    /// Reports whether this block draws anything where it stands.
    ///
    /// The exporter asks this while walking down a column for its surface.
    /// Stopping at the first block that is not air answers a different question. A
    /// large structure stands its real block beside a run of invisible
    /// placeholders, so a column stopping on one recorded a block with nothing to
    /// draw where there was grass, and the map painted black specks through
    /// finished terrain.
    ///
    /// The answer comes from the palette rather than the block, because the
    /// palette was built to answer it and a second implementation would disagree
    /// eventually. It moves when the palette moves, so a better palette from a
    /// client corrects what gets exported as well as what gets coloured.
    ///
    /// A block the palette says nothing about counts as showing.
    /// </summary>
    public bool Shows(int id) => !_hidden.Contains(id);

    /// <summary>
    /// Carries what this side could build, before it has anywhere to put it.
    ///
    /// The build happens at asset load, which is the only window, because the
    /// server frees block textures once assets are loaded. Where the palette goes
    /// is settled later, because the map may be filed per world and which world is
    /// running is not known this early.
    /// </summary>
    public sealed record Raw(Palette Palette, PaletteReport Report);

    /// <summary>Builds a palette from the blocks while their textures are still in memory.</summary>
    public static Raw BuildFromAssets(ICoreAPI api)
    {
        var built = PaletteBuilder.Build(api, out var report);
        return new Raw(built, report);
    }

    /// <summary>
    /// Reconciles what was built with whatever is already on disk, and writes it.
    ///
    /// Call this once the world is up and the map's directory is settled. It is
    /// separate from the build above because the build must happen while the
    /// textures are there and this cannot happen until there is somewhere to
    /// write.
    /// </summary>
    public static Built Settle(ICoreAPI api, string exports, Raw raw)
    {
        var (built, report) = (raw.Palette, raw.Report);

        var path = Palette.PathIn(exports);
        var existing = Palette.Read(path, api.Logger);

        // A stored colour is keyed on a block id, so the fingerprint decides
        // whether those colours still mean anything. The fingerprint covers the
        // block registry and nothing else. Nothing else may discard them.
        var valid = existing is not null && existing.Fingerprint == built.Fingerprint;

        // A moved mod stamp means some mod's textures may have changed under
        // colours that are still keyed correctly, which makes them stale rather
        // than wrong. Throwing the palette away for that cost a dedicated server
        // every colour it had on the next restart, because it builds itself one
        // with none, and the map drew nothing above its stored zoom levels until
        // an admin joined. The colours are kept and an admin is asked for a
        // fresh set instead.
        var moved = valid && existing!.ModStamp.Length > 0 && existing.ModStamp != built.ModStamp;

        var palette = valid ? Palette.Merge(existing!, built) : built;
        if (valid)
        {
            // Record the mod set as it stands, so one move of it costs one ask
            // rather than an ask on every start thereafter.
            palette.ModStamp = built.ModStamp;
        }

        // Fill what is still without a colour from the base game's recording.
        // This runs last, because a colour worked out from real assets on this
        // server or a player's machine beats a recording of somebody else's. On
        // a dedicated server running the base game that fills nearly everything,
        // and on a full install nearly nothing.
        var seeded = Vanilla.Fill(api, palette);

        var written = palette.Write(path);

        // Decide here, once, whether a client is asked. A palette a client built
        // for this registry with the mod set where it was stays this server's
        // colours until an admin says otherwise. A texture changing under an
        // unmoved block id and an unmoved mod set is the case nothing here can
        // see, and the case `/witchlight palette` exists for.
        var needed = NeedsAClient(palette, built.Fingerprint, moved);

        Report(api, exports, palette, existing, valid, moved, written, report, needed);
        Vanilla.Report(api, seeded);
        return new Built(palette, built.Fingerprint, needed);
    }

    /// <summary>
    /// Asks whoever in the room is best placed to answer, while no client has.
    ///
    /// This is the only unprompted ask. It runs on the export beat and again as
    /// each player joins, which makes a joining admin the person asked rather than
    /// whoever was already standing about. It stops for good the moment a whole
    /// palette arrives. See <see cref="Accept"/>.
    ///
    /// An admin is asked first. Theirs is the tileset the map should look like, so
    /// a colour taken from a player is one an admin's answer will have to replace.
    /// Where there is no admin, one of the others is picked at random rather than
    /// by connection order, so a server does not put the same person's client to
    /// work every time it comes up.
    ///
    /// Anybody with the mod may be asked. `commands.palette` says who may start a
    /// request by hand and nothing about whom one may be sent to. A client
    /// answering only lends its textures, and the guard on the map is what happens
    /// to the answer.
    ///
    /// Only one ask stands at a time, because the table is a few hundred kilobytes
    /// and every copy after the first is discarded.
    /// </summary>
    public void AskAround(IEnumerable<IServerPlayer> playing)
    {
        if (!_needed)
        {
            return;
        }

        if (_askedUid is not null && DateTime.UtcNow - _askedAt < AskAgainAfter)
        {
            return;
        }

        var room = playing
            .Where(player => !_asked.Contains(player.PlayerUID))
            .ToList();
        var admins = room.Where(player => player.HasPrivilege(Privilege.controlserver)).ToList();

        if ((OneOf(admins) ?? OneOf(room)) is { } somebody)
        {
            Ask(somebody, ForColours);
        }
    }

    /// <summary>
    /// Returns one of them chosen at random, or null where there are none.
    ///
    /// The choice is random rather than first because the server hands over its
    /// players in an order that is stable across a restart. Taking the front of
    /// that list would make one player's client answer for the whole server for
    /// as long as they keep playing there.
    /// </summary>
    private IServerPlayer? OneOf(IReadOnlyList<IServerPlayer> among) =>
        among.Count == 0 ? null : among[_api.World.Rand.Next(among.Count)];

    /// <summary>
    /// Asks now, whatever the current palette looks like and whoever was asked
    /// last. `/witchlight palette` calls this, and it is the only way a palette is
    /// asked for again once a client has supplied one.
    /// </summary>
    public void AskAnyway(IServerPlayer player)
    {
        _asked.Clear();
        _askedUid = null;
        _askedAt = DateTime.MinValue;
        Ask(player, ForColours);
    }

    private void Ask(IServerPlayer player, Reason why)
    {
        _askedUid = player.PlayerUID;
        _askedAt = DateTime.UtcNow;
        _asked.Add(player.PlayerUID);
        _api.Network.GetChannel(Channel.Name)
            .SendPacket(new PaletteRequest { Fingerprint = Fingerprint }, player);
        _api.Logger.Notification("[witchlight] asked {0} {1}", player.PlayerName, why.Asked);

        // Report silence, which otherwise looks like success in the log.
        var uid = player.PlayerUID;
        var name = player.PlayerName;
        _api.Event.RegisterCallback(_ =>
        {
            if (_askedUid == uid)
            {
                _api.Logger.Notification(
                    "[witchlight] no palette came back from {0}, who was asked {1} — {2}. Check "
                    + "that they have the mod, and their client log for why it declined",
                    name, why.Asked, why.Missed);
            }
        }, ReplyWaitMs);
    }

    /// <summary>
    /// Takes a palette slice from a client and keeps whatever the whole palette
    /// adds.
    ///
    /// The result is merged rather than replaced. A client only has textures for
    /// the mods it has installed, so two players with different mod sets can
    /// produce a complete palette where neither could alone.
    ///
    /// Whose colours win depends on who sent them. An admin's palette is preferred
    /// over what is stored, because an admin correcting the map is why
    /// `/witchlight palette` exists. Anybody else's fills gaps only, so a player
    /// cannot repaint a map somebody set up.
    /// </summary>
    public void Accept(IServerPlayer player, PaletteTable table)
    {
        // Accept a palette only from a player this server asked. The request is
        // what `commands.palette` gates, so a client sending one unasked would
        // be a way round that gate.
        if (!_asked.Contains(player.PlayerUID))
        {
            _api.Logger.Warning(
                "[witchlight] ignored a palette from {0}, who was not asked for one",
                player.PlayerName);
            _incoming.Remove(player.PlayerUID);
            return;
        }

        if (!Sane(player, table))
        {
            return;
        }

        if (table.Fingerprint != Fingerprint)
        {
            _api.Logger.Warning(
                "[witchlight] ignored a palette from {0}: it was built for a different mod set",
                player.PlayerName);
            return;
        }

        // Slices arrive one packet at a time. Nothing is written until the whole
        // palette is here.
        if (!_incoming.TryGetValue(player.PlayerUID, out var slices))
        {
            slices = new List<PaletteTable>();
            _incoming[player.PlayerUID] = slices;
        }

        slices.RemoveAll(existing => existing.Part == table.Part);
        slices.Add(table);

        if (slices.Count < table.Parts)
        {
            return;
        }

        _incoming.Remove(player.PlayerUID);

        var trusted = player.HasPrivilege(Privilege.controlserver);
        var path = Palette.PathIn(_exports);
        var supplied = PaletteTable.Assemble(slices, id => _api.World.GetBlock(id)?.Code?.ToString());
        var existing = Palette.Read(path, _api.Logger);

        // An admin sending a palette tells the map what it should look like. The
        // flag is recorded whether or not a colour moves, so the next admin to
        // join is not asked the same question again.
        supplied.FromAdmin = trusted;

        var merged = existing is not null && existing.Fingerprint == Fingerprint
            // An admin's colours win. Anybody else's only fill the gaps.
            ? (trusted ? Palette.Merge(supplied, existing) : Palette.Merge(existing, supplied))
            : supplied;

        // Record the source whichever way round the merge went. A reader of
        // `status` wants to know whether these colours came off a client's
        // assets, and after either merge they did.
        merged.Source = supplied.Source;

        var written = merged.Write(path);
        var before = _gaps;
        Settled(merged);
        _askedUid = null;
        // A client has answered for this mod set, which ends the asking until a
        // mod moves or an admin runs the command.
        _needed = false;

        _api.Logger.Notification(
            "[witchlight] palette from {0}{1}: {2} of {3} blocks coloured ({4:P0}){5}{6}",
            player.PlayerName,
            trusted ? "" : " (not an admin, so it only filled gaps)",
            merged.Coloured,
            merged.Textured,
            merged.Coverage,
            written ? "" : ", unchanged — the map was not redrawn",
            "; nobody else is asked until a mod changes or `/witchlight palette` is run");

        ReportGaps(before);
    }

    /// <summary>
    /// Logs what the map still cannot draw, once somebody has answered.
    ///
    /// The codes are named rather than counted, because they are what an operator
    /// can act on. A block nobody's client can colour is a block whose mod ships
    /// no texture for it, which is a report to make to that mod. This runs once
    /// per answer, and the asking stops with the first answer, so a gap nobody can
    /// fill is reported rather than chased.
    /// </summary>
    private void ReportGaps(IReadOnlyList<string> before)
    {
        if (_gaps.Count == 0)
        {
            if (before.Count > 0)
            {
                _api.Logger.Notification(
                    "[witchlight] every block the map can be asked to draw now has a colour");
            }
            return;
        }

        _api.Logger.Notification(
            "[witchlight] {0} block(s) still have no colour and will draw as bare ground: {1}{2}",
            _gaps.Count,
            string.Join(", ", _gaps.Take(MostNamed)),
            _gaps.Count > MostNamed ? $", and {_gaps.Count - MostNamed} more" : "");
    }

    /// <summary>Sets how many missing blocks to name in a log line.</summary>
    private const int MostNamed = 12;

    /// <summary>
    /// Reports whether this slice could be part of a palette for this server.
    ///
    /// The sender need not be an admin, so this bounds what a client can claim. A
    /// part outside its own total, a total larger than this server's registry
    /// could produce, or more slices held than that total allows each mean a
    /// client growing this process rather than sending a palette. Any of them
    /// ends the whole attempt rather than only that packet.
    /// </summary>
    private bool Sane(IServerPlayer player, PaletteTable table)
    {
        var held = _incoming.TryGetValue(player.PlayerUID, out var slices) ? slices.Count : 0;
        if (table.Parts > 0
            && table.Parts <= _mostSlices
            && table.Part >= 0
            && table.Part < table.Parts
            && held < _mostSlices)
        {
            return true;
        }

        _incoming.Remove(player.PlayerUID);
        _api.Logger.Warning(
            "[witchlight] dropped a palette from {0}: part {1} of {2} is not one this server "
            + "could have asked for (at most {3} parts)",
            player.PlayerName, table.Part, table.Parts, _mostSlices);
        return false;
    }

    /// <summary>
    /// Forgets a half-sent palette when the player sending it has gone.
    ///
    /// Slices are held until the set is complete, and a set that never completes
    /// would sit in memory for the life of the server.
    /// </summary>
    public void Forget(string uid) => _incoming.Remove(uid);

    /// <summary>
    /// Returns one line for `/witchlight status`.
    ///
    /// The line carries both fingerprints where they disagree. The palette's own
    /// says what it was built for and the server's says what it would have to be
    /// built for now, and the difference between them is why a map draws nothing.
    /// </summary>
    public string Describe()
    {
        if (Palette.Read(Palette.PathIn(_exports), _api.Logger) is not { } palette)
        {
            return $"palette: none written yet, for registry {Fingerprint}";
        }

        var gaps = palette.Uncoloured;
        return $"palette: from {palette.Source}, {palette.Coloured} of {palette.Blocks.Count} blocks "
            + $"coloured ({palette.Coverage:P0}), for registry {palette.Fingerprint}"
            + (palette.FromAdmin ? ", settled by an admin" : "")
            + (palette.Fingerprint == Fingerprint ? "" : $" — STALE, this server is {Fingerprint}")
            + (gaps.Count == 0
                ? ""
                : $"\n  no colour for {gaps.Count} block(s) that draw, so they map as bare ground: "
                  + string.Join(", ", gaps.Take(MostNamed))
                  + (gaps.Count > MostNamed ? $", and {gaps.Count - MostNamed} more" : ""));
    }

    /// <summary>
    /// Logs what the bootstrap found, and warns where colours were lost.
    /// </summary>
    private static void Report(
        ICoreAPI api,
        string exports,
        Palette palette,
        Palette? existing,
        bool valid,
        bool moved,
        bool written,
        PaletteReport report,
        bool needed)
    {
        if (moved)
        {
            api.Logger.Notification(
                "[witchlight] a mod moved since these {0} colours were built — keeping them "
                + "until a client supplies a fresh set, which the next player to join is asked for.",
                palette.Coloured);
        }

        // Warn here, because this is the one case where colours are lost and the
        // map goes flat until somebody joins to fix it.
        if (!valid && existing is not null && existing.Coloured > palette.Coloured)
        {
            api.Logger.Warning(
                "[witchlight] the block registry changed, so the stored palette's {0} colours no "
                + "longer match this world's block ids and have been replaced by this server's "
                + "own ({1} colours). The map draws nothing until a player joins and supplies one.",
                existing.Coloured,
                palette.Coloured);
        }

        if (needed && !moved)
        {
            api.Logger.Notification(
                "[witchlight] no client has supplied a palette for this mod set ({0:P0} of blocks "
                + "have a colour) — the next player to join will be asked for one",
                palette.Coverage);
        }

        api.Logger.Notification(
            written
                ? "[witchlight] palette: {0} blocks written to {1}{2}"
                : "[witchlight] palette: {0} blocks, unchanged — {1} not rewritten{2}",
            palette.Blocks.Count,
            Palette.PathIn(exports),
            report.Missing > 0 ? $" ({report.Missing} without a readable texture)" : "");

        foreach (var group in Order(report.Counts))
        {
            api.Logger.Notification("[witchlight] missing group {0}: {1}", group.Key, group.Value);
        }
        foreach (var failure in report.Failures)
        {
            api.Logger.Notification("[witchlight] skipped {0}", failure);
        }
    }

    /// <summary>Returns the dozen groups that lost the most colours.</summary>
    private static IEnumerable<KeyValuePair<string, int>> Order(IReadOnlyDictionary<string, int> counts)
    {
        var ordered = new List<KeyValuePair<string, int>>(counts);
        ordered.Sort((a, b) => b.Value.CompareTo(a.Value));
        return ordered.GetRange(0, Math.Min(12, ordered.Count));
    }
}
