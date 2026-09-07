using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Server;

namespace Witchlight;

/// <summary>
/// Asks players to draw themselves, and takes the pictures that come back.
///
/// Only a player's own client can draw them, because no other machine has that
/// seraph loaded. That is why a picture travels rather than a description.
///
/// Everybody is asked and everybody may send. A portrait decides what one card
/// looks like, and that card is the sender's own. The palette and the marker
/// pictures decide what everybody sees, so they let anybody fill a gap and only an
/// admin replace what is there.
///
/// This opens a write to every client, so this class bounds how often a picture
/// may arrive and <see cref="Portraits"/> bounds how large one may be.
/// </summary>
public sealed class PortraitExchange(ICoreServerAPI api, string exports)
{
    /// <summary>
    /// How long after a join before a player is asked to draw themselves.
    ///
    /// The server calls a player playing before their client has finished loading,
    /// and a seraph that is not loaded yet renders as an empty picture.
    /// </summary>
    private const int AskDelayMs = 8000;

    /// <summary>
    /// How long a request stands before the server stops expecting an answer.
    ///
    /// A client draws on its next frame, so an answer is normally back in well
    /// under a second. This covers a busy client and is short enough that an
    /// unanswered request does not excuse a much later picture sent for another
    /// reason.
    /// </summary>
    private const int AnswerWindowMs = 60000;

    private readonly ICoreServerAPI _api = api;
    private readonly string _exports = exports;

    /// <summary>The players asked for a picture who have not yet answered.</summary>
    private readonly Recent _asked = new(AnswerWindowMs);

    /// <summary>When each player's last unrequested picture arrived.</summary>
    private readonly Recent _taken = new(Portraits.FloorMs);

    /// <summary>
    /// Asks a joining player to draw themselves, after a pause, when the map has
    /// no picture of them yet.
    ///
    /// Asks only when there is no stored picture. <see cref="PortraitWatch"/> on
    /// the client redraws a player whose seraph changes, so a join with a picture
    /// already stored has nothing to ask for. The pause avoids an empty portrait
    /// from a client that has not finished loading.
    /// </summary>
    public void AskOnceSettled(IServerPlayer player, Action<string, Action> safely)
    {
        if (Portraits.StoredFor(_exports, player.PlayerUID) is not null)
        {
            return;
        }

        _api.Event.RegisterCallback(_ => safely("asking for a portrait", () =>
        {
            // They may have left during the pause.
            if (player.ConnectionState == EnumClientState.Playing)
            {
                Ask(player);
            }
        }), AskDelayMs);
    }

    /// <summary>
    /// Asks one player to draw themselves, and records the request.
    ///
    /// The only place a request is sent, so a picture arriving afterwards is
    /// recognisable as an answer. Nothing else writes to that table, which is what
    /// makes it safe to let an answer past the rate floor.
    /// </summary>
    public void Ask(IServerPlayer player)
    {
        _asked.Note(player.PlayerUID);
        _api.Network.GetChannel(Channel.Name).SendPacket(new PortraitRequest(), player);
    }

    /// <summary>
    /// Takes a player's picture of themselves.
    ///
    /// Files it under who sent it rather than under anything in the message. A
    /// client says what it looks like, not who it is.
    /// </summary>
    public void Accept(IServerPlayer player, PlayerPortrait portrait)
    {
        // Accept a picture the server asked for however soon after the last it
        // lands. A player who joins and then types the command a second later has
        // done nothing wrong. The rate floor applies only to what a client sends
        // of its own accord.
        if (!_asked.Take(player.PlayerUID))
        {
            if (_taken.Since(player.PlayerUID) is { } wait)
            {
                _api.Logger.Warning(
                    "[witchlight] ignored a portrait from {0}, sent unasked {1:0.#}s after "
                    + "the last one; unasked pictures are taken no closer together than {2}s",
                    player.PlayerName, wait.TotalSeconds, Portraits.FloorMs / 1000);
                return;
            }

            _taken.Note(player.PlayerUID);
        }

        if (Portraits.Save(_exports, player.PlayerUID, portrait.Png, out var said))
        {
            _api.Logger.Notification("[witchlight] portrait from {0} ({1})", player.PlayerName, said);
        }
        else
        {
            _api.Logger.Warning(
                "[witchlight] ignored a portrait from {0}: {1}", player.PlayerName, said);
        }
    }
}

/// <summary>
/// Records when something last happened for each key, within a bounded window.
///
/// Answers two questions: has this player been asked for a picture, and did this
/// player send one too recently.
///
/// Drops anything older than the window whenever the table is touched, so it holds
/// recent events rather than one entry per player the server has ever seen.
/// </summary>
public sealed class Recent
{
    private readonly Dictionary<string, DateTime> _at = new(StringComparer.Ordinal);
    private readonly TimeSpan _window;

    public Recent(int windowMs)
    {
        _window = TimeSpan.FromMilliseconds(windowMs);
    }

    /// <summary>Records that it has just happened for this key.</summary>
    public void Note(string key)
    {
        Forget();
        _at[key] = DateTime.UtcNow;
    }

    /// <summary>
    /// Returns how long ago it happened, or null when that is outside the window.
    /// </summary>
    public TimeSpan? Since(string key)
    {
        Forget();
        return _at.TryGetValue(key, out var when) ? DateTime.UtcNow - when : null;
    }

    /// <summary>
    /// Returns true when it happened inside the window, and removes the record.
    /// One recorded event is consumed once rather than for the whole window.
    /// </summary>
    public bool Take(string key)
    {
        Forget();
        return _at.Remove(key);
    }

    private void Forget()
    {
        var cutoff = DateTime.UtcNow - _window;
        foreach (var stale in _at.Where(at => at.Value <= cutoff).Select(at => at.Key).ToList())
        {
            _at.Remove(stale);
        }
    }
}
