using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.GameContent;

namespace Witchlight;

/// <summary>
/// The client half of Witchlight.
///
/// The server asks a client for three things it cannot produce itself: a picture
/// of its own player, a block colour palette, and the marker art. The client shows
/// one thing the server cannot show it: everybody else's markers on the in-game
/// map.
///
/// Sends nothing unprompted except a portrait, and that only once this player's
/// character has changed and settled.
/// </summary>
public partial class WitchlightClient : ModSystem
{
    private ICoreClientAPI? _capi;

    /// <summary>Draws this player when asked. Only their own client can.</summary>
    private PortraitCapture? _portrait;

    /// <summary>Reports when this player has changed and stopped changing.</summary>
    private PortraitWatch? _watch;

    public override bool ShouldLoad(EnumAppSide side) => side == EnumAppSide.Client;

    public override void StartClientSide(ICoreClientAPI api)
    {
        _capi = api;
        // Everyone else's markers, on this player's map, as a layer of this mod's
        // own. The game constructs it when the world loads, and what it shows is
        // handed to it as it arrives.
        api.ModLoader.GetModSystem<WorldMapManager>()
            .RegisterMapLayer<SharedMarkerMapLayer>(
                SharedMarkerMapLayer.Code, SharedMarkerMapLayer.Position);
        ListenForMarking(api);
        _portrait = new PortraitCapture(api);
        _watch = new PortraitWatch(api, () => SendPortrait(byHand: false));

        api.Network
            .RegisterChannel(Channel.Name)
            .Carrying()
            .SetMessageHandler<SharedMarkers>(message => SharedMarkerMapLayer.On(api)?.Take(message))
            .SetMessageHandler<PaletteRequest>(OnPaletteRequest)
            .SetMessageHandler<IconRequest>(OnIconRequest)
            .SetMessageHandler<PortraitRequest>(_ => SendPortrait(byHand: false))
            .SetMessageHandler<MarkReply>(TakeMarkReply)
            .SetMessageHandler<MarkNudge>(_ => AskToMark());

        api.ChatCommands
            .Create(Commands.Name)
            .WithShortName(api)
            .WithDescription("Witchlight map tools")
            .BeginSubCommand("mark")
                .WithDescription(
                    "Mark where you are looking, using the preset for that block")
                .HandleWith(OnMark)
            .EndSubCommand()
            .BeginSubCommand("portrait")
                .WithDescription("Send the map a picture of your character")
                .HandleWith(OnPortrait)
            .EndSubCommand()
            .BeginSubCommand("palette")
                .WithDescription("Build the block colour palette for the server you are on")
                .HandleWith(OnPalette)
            .EndSubCommand()
            .BeginSubCommand("icons")
                .WithDescription("Send the marker pictures to the server you are on")
                .HandleWith(OnIcons)
            .EndSubCommand();
    }
}
