using Vintagestory.API.Common;

namespace Witchlight;

/// <summary>
/// Names this mod's commands and declares them.
///
/// Registers a long name that is always there and a short name that is there when
/// nothing else has claimed it. Both sides register the same pair, so what a player
/// types reads the same whether the command runs on their own machine or the
/// server's. Only the game's prefix differs, which says which side answers.
/// </summary>
public static class Commands
{
    /// <summary>The name the command tree is registered under.</summary>
    public const string Name = "witchlight";

    /// <summary>The short name.</summary>
    public const string Short = "wl";

    /// <summary>
    /// Gives a command its short name, unless another mod already holds it.
    ///
    /// The game's <c>WithAlias</c> writes straight into the command table without
    /// looking, so it would take a name another mod holds and leave that mod broken
    /// with nothing saying where its command went.
    ///
    /// Losing the name costs only keystrokes, since the long name is registered
    /// either way and is the one the documentation gives.
    ///
    /// Checks each side separately. The game keeps a command table per side, so the
    /// short name can be free on a server and taken on a client connecting to it.
    /// </summary>
    public static IChatCommand WithShortName(this IChatCommand command, ICoreAPI api)
    {
        if (api.ChatCommands.Get(Short) is not null)
        {
            api.Logger.Notification(
                "[witchlight] something already answers to {0}, so the commands are only "
                + "under {1}", Short, Name);
            return command;
        }

        return command.WithAlias(Short);
    }

    /// <summary>
    /// Opens a subcommand under the privilege the settings give it.
    ///
    /// Takes the name once and hands the same string to the game's command table
    /// and to <see cref="Permissions"/>, which key on it. A command registered
    /// under a permission nobody configured has a setting that does nothing.
    /// </summary>
    public static IChatCommand BeginSubCommand(
        this IChatCommand tree, string name, string description) =>
        tree.BeginSubCommand(name)
            .WithDescription(description)
            .RequiresPrivilege(Permissions.For(name));
}
