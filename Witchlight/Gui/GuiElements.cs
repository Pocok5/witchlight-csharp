using System;
using Vintagestory.API.Client;

namespace Witchlight;

/// <summary>
/// The pieces both marker windows are built from.
///
/// Two windows edit a marker: the one a marker is made in from in game, and the one
/// that opens on somebody else's marker from the map. Both ask the same first three
/// questions in the same order, with the same words and the same spacing, and both
/// then put switches under them. This holds that block and the measurements around
/// it, so the two windows cannot come out saying different things or sitting at
/// different heights.
///
/// It owns the layout and the wording. Each window keeps its own answers, because
/// the two hold a colour differently: one holds the hex the map service reads, the
/// other the packed number the game hands back.
/// </summary>
public static class GuiElements
{
    /// <summary>
    /// Bounds a list picker's index before it is used.
    ///
    /// The game hands back whatever was clicked, and a picker whose list changed
    /// under it can hand back an index past the end. Both pickers call this.
    /// </summary>
    public static int Within(int index, int many) => index < 0 || index >= many ? 0 : index;

    /// <summary>The words above each of the three fields. Stated once, so a window
    ///  cannot label a control differently from the other window's copy of it.</summary>
    public const string NameLabel = "Name";
    public const string ColourLabel = "Colour";
    public const string PictureLabel = "Marker Icon";

    /// <summary>The game's own switch height, padding and column width. Stated once
    ///  so no two switch rows come out at different heights.</summary>
    public const int SwitchSize = 30;
    public const int SwitchPad = 4;
    public const int SwitchWidth = 40;

    /// <summary>The element keys both windows read their controls back by.</summary>
    public const string NameInput = "nameInput";
    public const string ColourPicker = "colorPicker";
    public const string PicturePicker = "iconPicker";

    /// <summary>How wide the name field is, and how wide a row of swatches runs
    ///  before it wraps.</summary>
    private const int FieldWidth = 220;
    private const int PickerWidth = 270;

    /// <summary>Returns the bounds for a switch's label, level with the line of text
    ///  the switch is centred on and clear of the box by the standard gap.</summary>
    public static ElementBounds Beside(ElementBounds toggle) =>
        ElementBounds.Fixed(toggle.fixedX + SwitchWidth + 6, toggle.fixedY + 4, 280, 25);

    /// <summary>
    /// Adds the name field and the two pickers, under a name label the caller has
    /// already placed.
    ///
    /// The caller places the name label itself, because one window puts a field
    /// beside it and the other sometimes puts a line of plain text there instead.
    /// From the field down, both windows are identical, which is what this adds.
    ///
    /// `label` is the row the name label occupies on the way in, and the row the
    /// picture picker ended on on the way out. A picker wraps to as many rows as the
    /// mods on the server give it, so its height is not known until it is placed,
    /// and the caller needs the row it landed on to put the switches under it.
    ///
    /// `field` is advanced to the name field in the same way, for a caller measuring
    /// against it.
    ///
    /// Each picker reports the index that was clicked. The caller reads it against
    /// the same list it passed in and keeps the answer in whatever form it stores.
    /// </summary>
    public static GuiComposer AddMarkerFields(
        this GuiComposer composer,
        ref ElementBounds label,
        ref ElementBounds field,
        int[] colours,
        string[] pictures,
        Action<int> onColour,
        Action<int> onPicture)
    {
        return composer
            .AddTextInput(field = field.FlatCopy().WithFixedWidth(FieldWidth), _ => { },
                CairoFont.TextInput(), NameInput)

            .AddStaticText(ColourLabel, CairoFont.WhiteSmallText(),
                label = label.BelowCopy(0, 9))
            .AddColorListPicker(colours, onColour,
                label = label.BelowCopy(0, 5).WithFixedSize(22, 22), PickerWidth, ColourPicker)

            .AddStaticText(PictureLabel, CairoFont.WhiteSmallText(),
                label = label.WithFixedPosition(0, label.fixedY + label.fixedHeight)
                    .WithFixedWidth(200).BelowCopy())
            .AddIconListPicker(pictures, onPicture,
                label = label.BelowCopy(0, 5).WithFixedSize(27, 27), PickerWidth, PicturePicker);
    }

    /// <summary>
    /// Returns where the first switch goes, under whatever height the picture picker
    /// came out at.
    ///
    /// Four pixels up, so the switch and its label read as one row. A switch is
    /// taller than a line of text.
    /// </summary>
    public static ElementBounds UnderPickers(ElementBounds pictures) =>
        ElementBounds.Fixed(
            0,
            pictures.fixedY + pictures.fixedHeight * 2 + 9 - 4,
            SwitchWidth,
            SwitchSize);
}
