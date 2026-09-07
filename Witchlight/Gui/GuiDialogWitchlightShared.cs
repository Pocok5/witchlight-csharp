using System;
using System.Linq;
using Vintagestory.API.Client;
using Vintagestory.GameContent;

namespace Witchlight;

/// <summary>
/// The window that opens on somebody else's marker from the in-game map.
///
/// The game's own edit window cannot open here, because it names a waypoint by its
/// place in the player's own list and a shared marker has none. This names it by
/// key.
///
/// Offers what the server said this player may do. Anybody the marker is shared
/// with may pin it on their own map, which changes nothing anybody else sees. Only
/// a player the server said may change it is shown the name, the colour and the
/// picture to change. Everybody else reads the name and decides about the pin.
///
/// The colours and pictures come from the game's waypoint layer, so this offers
/// exactly what the game's own window offers.
/// </summary>
public class GuiDialogWitchlightShared : GuiDialogGeneric
{
    private const string Composed = "witchlight-shared";
    private const string NameInput = "nameInput";
    private const string ColourPicker = "colorPicker";
    private const string PicturePicker = "iconPicker";
    private const string PinSwitch = "pinSwitch";
    private const string PresetSwitch = "presetSwitch";

    /// <summary>The game's own size and padding for a switch.</summary>
    private const int SwitchSize = 30;
    private const int SwitchPad = 4;
    private const int SwitchWidth = 40;

    private readonly int[] _colours;
    private readonly string[] _pictures;
    private readonly Action<SharedMarkerChange> _send;

    private SharedMarker _marker;
    private string _picture;
    private int _colour;
    private bool _pinned;
    private bool _preset;

    public GuiDialogWitchlightShared(
        ICoreClientAPI capi, WaypointMapLayer layer, SharedMarker marker, Action<SharedMarkerChange> send)
        : base("", capi)
    {
        _colours = layer.WaypointColors.ToArray();
        _pictures = layer.WaypointIcons.Keys.ToArray();
        _send = send;
        _marker = marker;
        _pinned = marker.Pinned;
        _picture = _pictures.Contains(marker.Icon) ? marker.Icon : (_pictures.FirstOrDefault() ?? Markers.PlainIcon);
        // Keep a colour the game does not offer. The picker shows its first
        // swatch, and saving without touching it keeps the marker's colour rather
        // than the swatch the picker landed on.
        _colour = marker.Color;
    }

    /// <summary>The marker this window is open on.</summary>
    public string Key => _marker.Key;

    /// <summary>
    /// Declares this a dialog rather than a piece of the world map, so it takes the
    /// mouse the way every other window does and closes on the same key.
    /// </summary>
    public override bool PrefersUngrabbedMouse => true;

    /// <summary>
    /// Draws over the world map, which is where this opens from. The game draws its
    /// own waypoint windows at this order too. At the default order a window draws
    /// under the map and only the edge past the map shows.
    /// </summary>
    public override double DrawOrder => 0.2;

    public override bool TryOpen()
    {
        Compose();
        return base.TryOpen();
    }

    /// <summary>
    /// Takes the marker as the server now says it is. Reads only the pin, which is
    /// what this window asked for and is waiting to see land. Anything the player
    /// is in the middle of typing stays as they typed it.
    /// </summary>
    public void Shown(SharedMarker marker)
    {
        _marker = marker;
        if (_pinned != marker.Pinned)
        {
            _pinned = marker.Pinned;
            SingleComposer?.GetSwitch(PinSwitch)?.SetValue(_pinned);
        }
    }

    private void Compose()
    {
        var label = ElementBounds.Fixed(0, 28, 100, 25);
        var field = label.RightCopy();
        var row = ElementBounds.Fixed(0, 28, 360, 25);
        var toggle = ElementBounds.Fixed(0, 28, SwitchWidth, SwitchSize);

        var inside = ElementBounds.Fill.WithFixedPadding(GuiStyle.ElementToDialogPadding);
        inside.BothSizing = ElementSizing.FitToChildren;
        inside.WithChildren(label, field);

        SingleComposer?.Dispose();
        var compo = capi.Gui
            .CreateCompo(Composed, ElementStdBounds.AutosizedMainDialog
                .WithAlignment(EnumDialogArea.RightMiddle)
                .WithFixedAlignmentOffset(-GuiStyle.DialogToScreenPadding, 0))
            .AddShadedDialogBG(inside, false)
            .AddDialogTitleBar(Heading(), () => TryClose())
            .BeginChildElements(inside)
            .AddStaticText("Name", CairoFont.WhiteSmallText(), label = label.FlatCopy());

        if (_marker.Editable)
        {
            compo = compo
                .AddTextInput(field = field.FlatCopy().WithFixedWidth(220), _ => { },
                    CairoFont.TextInput(), NameInput)
                .AddStaticText("Colour", CairoFont.WhiteSmallText(),
                    label = label.BelowCopy(0, 9))
                .AddColorListPicker(_colours, OnColour,
                    label = label.BelowCopy(0, 5).WithFixedSize(22, 22), 270, ColourPicker)
                .AddStaticText("Picture", CairoFont.WhiteSmallText(),
                    label = label.WithFixedPosition(0, label.fixedY + label.fixedHeight)
                        .WithFixedWidth(200).BelowCopy())
                .AddIconListPicker(_pictures, OnPicture,
                    label = label.BelowCopy(0, 5).WithFixedSize(27, 27), 270, PicturePicker);
            toggle = ElementBounds.Fixed(
                0, label.fixedY + label.fixedHeight * 2 + 9 - 4, SwitchWidth, SwitchSize);
        }
        else
        {
            compo = compo.AddStaticText(Markers.Title(_marker.Title), CairoFont.WhiteSmallText(),
                field = field.FlatCopy().WithFixedWidth(220));
            toggle = ElementBounds.Fixed(0, label.fixedY + label.fixedHeight + 9, SwitchWidth, SwitchSize);
        }

        var keep = toggle.BelowCopy(0, 6);
        SingleComposer = compo
            // The pin is the one thing anybody may decide about somebody else's
            // marker. It holds the marker against the edge of their own map
            // instead of letting it scroll off, and no other map changes.
            .AddSwitch(on => _pinned = on, toggle, PinSwitch, SwitchSize, SwitchPad)
            .AddStaticText("Pin marker", CairoFont.WhiteSmallText(), Beside(toggle))
            // They may also copy its name, picture and colour into a preset of
            // their own for the block it was made on. The marker's owner does not
            // change.
            .AddSwitch(on => _preset = on, keep, PresetSwitch, SwitchSize, SwitchPad)
            .AddStaticText("Save as preset", CairoFont.WhiteSmallText(), Beside(keep))
            .AddSmallButton("Cancel", OnCancel,
                row.FlatCopy().FixedUnder(keep, 30).WithFixedWidth(100),
                EnumButtonStyle.Normal)
            .AddSmallButton("Save", OnSave,
                row.FlatCopy().FixedUnder(keep, 30).WithFixedWidth(100)
                    .WithAlignment(EnumDialogArea.RightFixed),
                EnumButtonStyle.Normal)
            .EndChildElements()
            .Compose();

        SingleComposer.GetSwitch(PinSwitch).SetValue(_pinned);
        SingleComposer.GetSwitch(PresetSwitch).SetValue(_preset);
        if (_marker.Editable)
        {
            SingleComposer.GetTextInput(NameInput).SetValue(Markers.Title(_marker.Title));
            SingleComposer.ColorListPickerSetValue(ColourPicker, Math.Max(0, Array.IndexOf(_colours, _colour)));
            SingleComposer.IconListPickerSetValue(PicturePicker, Math.Max(0, Array.IndexOf(_pictures, _picture)));
        }
    }

    /// <summary>Puts the owner's name on the title bar, so the name field holds the
    ///  marker's name alone.</summary>
    private string Heading() =>
        string.IsNullOrEmpty(_marker.Owner) ? "Shared marker" : $"{_marker.Owner}'s marker";

    /// <summary>Returns the bounds for the label beside a switch, level with the
    ///  line of text the switch is centred on.</summary>
    private static ElementBounds Beside(ElementBounds toggle) =>
        ElementBounds.Fixed(toggle.fixedX + SwitchWidth + 6, toggle.fixedY + 4, 280, 25);

    private void OnColour(int index)
    {
        _colour = _colours[GuiElements.Within(index, _colours.Length)];
    }

    private void OnPicture(int index)
    {
        _picture = _pictures[GuiElements.Within(index, _pictures.Length)];
    }

    private bool OnCancel()
    {
        TryClose();
        return true;
    }

    /// <summary>
    /// Sends what the window is holding and closes it. Changes nothing locally.
    /// The marker lives on the server, and the result arrives with the next share.
    /// </summary>
    private bool OnSave()
    {
        var change = new SharedMarkerChange
        {
            Key = _marker.Key,
            Pinned = _pinned,
            KeepPreset = _preset,
            Editing = _marker.Editable,
            Title = _marker.Title,
            Icon = _marker.Icon,
            Color = _marker.Color,
        };
        if (_marker.Editable)
        {
            change.Title = SingleComposer.GetTextInput(NameInput).GetText();
            change.Icon = _picture;
            change.Color = _colour;
        }
        _send(change);
        TryClose();
        return true;
    }
}
