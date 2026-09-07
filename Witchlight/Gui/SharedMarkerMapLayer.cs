using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.GameContent;

namespace Witchlight;

/// <summary>
/// Draws everyone else's markers on this player's in-game map, as a map layer of
/// this mod's own.
///
/// The game draws a player's own waypoints and nobody else's, and every way it
/// offers to add to the map adds to that one list. The list addresses a waypoint
/// by the asker's place in their own waypoints, and a marker that is not theirs
/// has no such place. The game's edit window sends an index one past the end and
/// the server refuses it.
///
/// So these markers stay out of that list. This layer draws them from what the
/// server sent, and a click opens this mod's own window, which names the marker by
/// its key and asks the server in this mod's own words.
///
/// The game constructs one of these when the world loads, through
/// <see cref="WorldMapManager.RegisterMapLayer{T}"/>, and hands it the map to draw
/// on. <see cref="Take"/> delivers what it shows. Nothing here writes to the
/// game's waypoint list.
/// </summary>
public sealed class SharedMarkerMapLayer : MapLayer
{
    /// <summary>The name the layer is registered under.</summary>
    public const string Code = "witchlight-shared";

    /// <summary>
    /// The draw order, between the players and the waypoints, so a shared marker
    /// draws under the player's own and the player's own take a click first.
    /// </summary>
    public const double Position = 0.9;

    private readonly ICoreClientAPI? _capi;

    /// <summary>The shared markers the server last sent, by key.</summary>
    private readonly Dictionary<string, SharedMarker> _shared = new(StringComparer.Ordinal);

    /// <summary>One component per marker, rebuilt when the set changes.</summary>
    private readonly List<SharedMarkerComponent> _drawn = new();

    /// <summary>
    /// A fingerprint of the set as last drawn, so an unchanged send does not
    /// redraw. The server sends every marker every fifteen seconds and almost none
    /// has moved.
    /// </summary>
    private string _shape = "";

    /// <summary>The window currently open on one of these markers, so a second
    ///  click does not put a second window over the first.</summary>
    private GuiDialogWitchlightShared? _dialog;

    public SharedMarkerMapLayer(ICoreAPI api, IWorldMapManager mapSink)
        : base(api, mapSink)
    {
        _capi = api as ICoreClientAPI;
    }

    public override string Title => "Shared markers";

    /// <summary>
    /// The game's own waypoint group, so the map switch that hides waypoints hides
    /// these with them.
    /// </summary>
    public override string LayerGroupCode => "waypoints";

    /// <summary>Runs on the client only. The server sends to it over the mod's own
    ///  channel.</summary>
    public override EnumMapAppSide DataSide => EnumMapAppSide.Client;

    /// <summary>Draws a marker wherever it is, explored or not.</summary>
    public override bool RequireChunkLoaded => false;

    /// <summary>The layer instance the game made, or null before it has.</summary>
    public static SharedMarkerMapLayer? On(ICoreClientAPI api) =>
        api.ModLoader.GetModSystem<WorldMapManager>()?.MapLayers?
            .OfType<SharedMarkerMapLayer>().FirstOrDefault();

    /// <summary>Takes what the server sent and redraws when it has changed.</summary>
    public void Take(SharedMarkers message)
    {
        _shared.Clear();
        foreach (var marker in message.Markers)
        {
            if (!string.IsNullOrEmpty(marker.Key))
            {
                _shared[marker.Key] = marker;
            }
        }

        var shape = Shape();
        if (shape == _shape)
        {
            return;
        }
        _shape = shape;
        Redraw();

        // A window open on a marker that just changed under it is showing what
        // the marker was. The only thing the server changes on its own is the pin
        // that window asked for, which is what it is waiting on.
        if (_dialog is { } dialog && dialog.IsOpened()
            && _shared.TryGetValue(dialog.Key, out var shown))
        {
            dialog.Shown(shown);
        }
    }

    /// <summary>
    /// Rebuilds one component per marker from what the server said.
    ///
    /// The pictures belong to the game's waypoint layer, which loads them when the
    /// map opens. A component built while the map is shut finds that out when it
    /// is asked to draw, so this can run whether the map is up or not.
    /// </summary>
    private void Redraw()
    {
        foreach (var drawn in _drawn)
        {
            drawn.Dispose();
        }
        _drawn.Clear();

        if (_capi is null || Markers.Layer(_capi) is not { } game)
        {
            return;
        }

        foreach (var marker in _shared.Values)
        {
            _drawn.Add(new SharedMarkerComponent(_capi, game, marker, Open));
        }

        _capi.Logger.Notification("[witchlight] {0} shared marker(s) on the map", _drawn.Count);
    }

    /// <summary>
    /// Returns a fingerprint of the set as one string. Sorted, because the order
    /// the server sends them in is not a change.
    /// </summary>
    private string Shape()
    {
        var said = _shared.Values
            .Select(marker =>
                $"{marker.Key}|{marker.X}|{marker.Y}|{marker.Z}|{marker.Title}|{marker.Icon}"
                + $"|{marker.Color}|{marker.Owner}|{marker.Pinned}|{marker.Editable}")
            .OrderBy(line => line, StringComparer.Ordinal);
        return string.Join("\n", said);
    }

    /// <summary>
    /// Opens this mod's window on one marker. The game's own window addresses a
    /// waypoint by its place in the asker's list, and a shared marker has none.
    /// </summary>
    private void Open(SharedMarker marker)
    {
        if (_capi is null || Markers.Layer(_capi) is not { } game)
        {
            return;
        }

        _dialog?.TryClose();
        _dialog = new GuiDialogWitchlightShared(_capi, game, marker, Send);
        _dialog.TryOpen();
        // Hand the mouse back to the map behind it when the window shuts, the way
        // the game's own window does.
        _dialog.OnClosed += () =>
        {
            if (_capi.ModLoader.GetModSystem<WorldMapManager>()?.worldMapDlg is { } map)
            {
                _capi.Gui.RequestFocus(map);
            }
        };
    }

    /// <summary>Clears the open window when it closes.</summary>
    private void Send(SharedMarkerChange change)
    {
        _capi?.Network.GetChannel(Channel.Name)?.SendPacket(change);
    }

    /// <summary>
    /// Rebuilds the components when the map opens. The game's waypoint layer
    /// reloads its pictures then, and a component holding the old ones would draw
    /// nothing.
    /// </summary>
    public override void OnMapOpenedClient() => Redraw();

    public override void Render(GuiElementMap mapElem, float dt)
    {
        if (!Active)
        {
            return;
        }
        foreach (var drawn in _drawn)
        {
            drawn.Render(mapElem, dt);
        }
    }

    public override void OnMouseMoveClient(MouseEvent args, GuiElementMap mapElem, StringBuilder hoverText)
    {
        if (!Active)
        {
            return;
        }
        foreach (var drawn in _drawn)
        {
            drawn.OnMouseMove(args, mapElem, hoverText);
        }
    }

    public override void OnMouseUpClient(MouseEvent args, GuiElementMap mapElem)
    {
        if (!Active)
        {
            return;
        }
        foreach (var drawn in _drawn)
        {
            drawn.OnMouseUpOnElement(args, mapElem);
            if (args.Handled)
            {
                return;
            }
        }
    }

    public override void Dispose()
    {
        foreach (var drawn in _drawn)
        {
            drawn.Dispose();
        }
        _drawn.Clear();
        _dialog?.TryClose();
        _dialog = null;
    }
}
