using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.GameContent;

namespace Witchlight;

/// <summary>
/// Marks a place from in game, without opening a map first.
///
/// Offers one press that marks what the player is looking at, the way they have
/// said that kind of thing is marked. A key and a chat command do the same thing,
/// because a key is unreachable while a chat box is open and a command is what
/// somebody reaches for the first time.
///
/// Decides nothing about what the marker becomes. The server holds the block at
/// the position and the map service holds what this player has kept, and
/// <see cref="Marking"/> reads both. This side works out where the player means
/// and what to do with the answer.
/// </summary>
public partial class WitchlightClient
{
    /// <summary>The key's name in the controls settings.</summary>
    private const string MarkHotkey = "witchlightmark";

    /// <summary>The window open where no preset had an answer, or null.</summary>
    private GuiDialogWitchlightMark? _marking;

    /// <summary>Registers the key and the command that ask for a marker.</summary>
    private void ListenForMarking(ICoreClientAPI api)
    {
        // `[` is unbound in a stock game, sits next to the other bracket for a
        // second binding to grow into, and is nowhere near the movement keys. A key
        // sharing a corner with WASD gets pressed by accident while moving.
        api.Input.RegisterHotKey(
            MarkHotkey,
            "Create from Witchlight preset",
            GlKeys.BracketLeft,
            HotkeyType.CharacterControls);
        api.Input.SetHotKeyHandler(MarkHotkey, _ => AskToMark());
    }

    /// <summary>
    /// Asks the server to mark where this player means, using a preset. Always
    /// returns true, because the key was handled, and the answer reports what came
    /// of it in chat.
    /// </summary>
    private bool AskToMark()
    {
        if (_capi is not { } api)
        {
            return true;
        }

        // A window already open is the answer to the last press. A second press
        // would put a second window over the first.
        if (_marking is not null && _marking.IsOpened())
        {
            return true;
        }

        api.Network.GetChannel(Channel.Name).SendPacket(Meaning(api));
        return true;
    }

    /// <summary>
    /// Returns where this player means and which block names it.
    ///
    /// A player looking at a block means that block, and both answers are it. A
    /// player looking at nothing means where they are standing, where the block is
    /// air, so the block under their feet names the marker.
    /// </summary>
    private static MarkAsk Meaning(ICoreClientAPI api)
    {
        var player = api.World.Player;
        if (player.CurrentBlockSelection is { } aimed)
        {
            var at = aimed.Position;
            return new MarkAsk
            {
                X = at.X + 0.5,
                Y = at.Y,
                Z = at.Z + 0.5,
                BlockX = at.X,
                BlockY = at.Y,
                BlockZ = at.Z,
                UsePreset = true,
            };
        }

        var standing = player.Entity.Pos;
        return new MarkAsk
        {
            X = standing.X,
            Y = standing.Y,
            Z = standing.Z,
            BlockX = Blocks.At(standing.X),
            BlockY = Blocks.At(standing.Y) - 1,
            BlockZ = Blocks.At(standing.Z),
            UsePreset = true,
        };
    }

    /// <summary>
    /// Handles the server's answer to a request.
    ///
    /// Reports a marker that was made as a line of chat, since the marker is
    /// already on the map and a window over it would be one more thing to close.
    /// Opens the window where no preset answered, filled in with everything the
    /// server worked out.
    /// </summary>
    private void TakeMarkReply(MarkReply reply)
    {
        if (_capi is not { } api)
        {
            return;
        }

        if (!reply.Yours)
        {
            api.ShowChatMessage("[Witchlight] " + reply.Said);
            return;
        }

        if (Markers.Layer(api) is not { } layer)
        {
            api.ShowChatMessage("[Witchlight] The map layer is not up, so there is nothing to mark on.");
            return;
        }

        // Open the map behind it, so the window opens over a picture of where the
        // marker is going rather than over the world. Toggling leaves a map that is
        // already up rather than shutting it.
        var maps = api.ModLoader.GetModSystem<WorldMapManager>();
        if (maps is { IsOpened: false })
        {
            maps.ToggleMap(EnumDialogType.Dialog);
        }

        _marking?.TryClose();
        _marking = new GuiDialogWitchlightMark(api, layer, reply, Send);
        _marking.TryOpen();
        api.ShowChatMessage("[Witchlight] " + reply.Said);
    }

    /// <summary>Clears the open window when it closes.</summary>
    private void Send(MarkAsk ask)
    {
        _capi?.Network.GetChannel(Channel.Name).SendPacket(ask);
    }

    /// <summary>
    /// Runs the chat command that does what the key does.
    ///
    /// Calls the same function the key does, so `/wl mark` and the key cannot come
    /// to mean different things. A key is the one to use and a command is the one
    /// somebody finds.
    /// </summary>
    private TextCommandResult OnMark(TextCommandCallingArgs args)
    {
        AskToMark();
        return TextCommandResult.Success("Marking…");
    }
}
