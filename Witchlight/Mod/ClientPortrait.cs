using System;
using Vintagestory.API.Common;

namespace Witchlight;

/// <summary>
/// Decides when to draw this player and sends the picture.
///
/// Only their own machine can draw them, since no other has that seraph loaded,
/// which is why the picture travels rather than a description.
/// <see cref="PortraitCapture"/> does the drawing and
/// <see cref="PortraitWatch"/> watches for a change.
/// </summary>
public partial class WitchlightClient
{
    /// <summary>
    /// How long between pictures a player asks for by hand.
    ///
    /// Long, because everything that keeps the map current happens without the
    /// command. Nobody waiting this out is waiting for their own face to appear.
    /// </summary>
    private static readonly TimeSpan CommandWait = TimeSpan.FromMinutes(5);

    /// <summary>When this player last had a picture they asked for by hand.</summary>
    private DateTime? _askedAt;

    /// <summary>
    /// Runs the command that draws this player and sends the picture to the server.
    ///
    /// Allowed once every <see cref="ByHandEvery"/>. Nothing about the map being
    /// current rests on the command: a picture is asked for on every join and sent
    /// again whenever a character has been left alone for half a minute. Refuses
    /// before drawing anything, so a player leaning on it costs the server nothing
    /// and their own machine no frames.
    /// </summary>
    private TextCommandResult OnPortrait(TextCommandCallingArgs args)
    {
        if (_capi is null || _portrait is null)
        {
            return TextCommandResult.Error("not connected to a world");
        }

        if (WaitLeft() is { } left)
        {
            return TextCommandResult.Error(
                $"you asked for one already — ask again in {Spoken(left)}. "
                + "The map draws you on every join and whenever you change anyway.");
        }

        SendPortrait(byHand: true);
        return TextCommandResult.Success("drawing your character...");
    }

    /// <summary>
    /// Draws this player and sends the picture.
    ///
    /// The drawing happens in a frame, so the result arrives after the caller has
    /// returned.
    /// </summary>
    /// <param name="byHand">
    /// True when a player typed the command and is waiting on an answer. Their
    /// result goes to chat rather than only to the log, and the wait before they
    /// may ask again starts. An unprompted picture reaches the screen not at all,
    /// since a picture is drawn on every join and after every change of clothes.
    /// </param>
    private void SendPortrait(bool byHand)
    {
        if (_capi is not { } capi || _portrait is not { } portrait)
        {
            return;
        }

        // The character as it stands has now been drawn, whatever prompted it, so
        // restart the wait from here rather than from a change this picture already
        // covers.
        _watch?.Settled();

        portrait.Take((png, said) =>
        {
            if (png is null)
            {
                capi.Logger.Warning("[witchlight] could not draw a portrait: {0}", said);
                if (byHand)
                {
                    capi.ShowChatMessage($"Witchlight: could not draw your portrait — {said}");
                }
                return;
            }

            // Count the wait from the picture rather than from the command, so an
            // attempt that drew nothing may be made again.
            if (byHand)
            {
                _askedAt = DateTime.UtcNow;
            }

            capi.Network.GetChannel(Channel.Name).SendPacket(new PlayerPortrait { Png = png });
            capi.Logger.Notification("[witchlight] sent a {0} byte portrait ({1})", png.Length, said);
            if (byHand)
            {
                // Report bytes, not kilobytes. A picture of nothing weighs a
                // hundred and fifty bytes and rounds to "0 KiB", which hides an
                // empty render.
                capi.ShowChatMessage($"Witchlight: sent your portrait, {png.Length} bytes — {said}");
            }
        });
    }

    /// <summary>
    /// Returns how long is left before this player may ask for another, or null
    /// when they may ask now.
    /// </summary>
    private TimeSpan? WaitLeft()
    {
        if (_askedAt is not { } last)
        {
            return null;
        }

        var left = CommandWait - (DateTime.UtcNow - last);
        return left > TimeSpan.Zero ? left : null;
    }

    /// <summary>Formats a wait the way somebody would say it, rather than as a
    ///  count of seconds.</summary>
    private static string Spoken(TimeSpan left) => left < TimeSpan.FromMinutes(1)
        ? $"{left.Seconds + 1}s"
        : $"{(int)left.TotalMinutes}m {left.Seconds}s";
}
