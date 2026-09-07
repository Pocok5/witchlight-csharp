using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using Vintagestory.GameContent;

namespace Witchlight;

/// <summary>
/// The window a marker is made in from in game.
///
/// The game's own window, reached by opening the map and right clicking, makes a
/// waypoint. This makes a Witchlight marker, which is the same waypoint plus two
/// answers the game has no idea about: who may see it, and whether this is what
/// that kind of block starts as from now on.
///
/// Opens on a press of the marking key over a block no preset names. Everything
/// arrives filled in from the server, so the ordinary case is reading a name and
/// pressing Save.
///
/// The colours and pictures come from the game's waypoint layer, so this offers
/// exactly what the game's own window offers, including anything a mod adds.
///
/// "Presets" opens a list of everything this player has kept beside the window.
/// Choosing one fills the name, the colour, the picture and who may see it, the
/// way pressing the key over that preset's own block would have. It does not
/// change the block the marker is made on, which is where the player is standing.
/// </summary>
public class GuiDialogWitchlightMark : GuiDialogGeneric
{
    private const string Composed = "witchlight-mark";
    private const string PresetsComposed = "witchlight-mark-presets";
    private const string PresetsScrollbar = "presetsScrollbar";
    private const string PresetsFind = "presetsFind";
    private const string PresetsRows = "presetsRows";
    private const string NameInput = "nameInput";
    private const string ColourPicker = "colorPicker";
    private const string PicturePicker = "iconPicker";
    private const string PrivateSwitch = "privateSwitch";
    private const string PresetSwitch = "presetSwitch";

    private readonly int[] _colours;
    private readonly string[] _pictures;
    private readonly MarkReply _offer;
    private readonly Action<MarkAsk> _send;

    private string _picture;
    private string _colour;
    private bool _private;
    private bool _preset;

    /// <summary>The rows of the preset list. Moved as the scrollbar moves.</summary>
    private ElementBounds? _presetRows;

    public GuiDialogWitchlightMark(
        ICoreClientAPI capi, WaypointMapLayer layer, MarkReply offer, Action<MarkAsk> send)
        : base("", capi)
    {
        _colours = layer.WaypointColors.ToArray();
        _pictures = layer.WaypointIcons.Keys.ToArray();
        _offer = offer;
        _send = send;

        _private = offer.Private == Mark.Private;
        _preset = offer.KeepPreset;
        _picture = Offered(_pictures, offer.Icon, Markers.PlainIcon);
        // Hold the colour of the swatch that will be selected, not the one the
        // server offered. A colour the game does not have leaves the picker on its
        // first swatch, and a window showing one colour while holding another
        // makes a marker nobody chose.
        _colour = _colours.Length > 0 ? Markers.Hex(_colours[Chosen(offer.Color)]) : offer.Color;
    }

    /// <summary>
    /// Declares this a dialog rather than a piece of the world map, so it takes the
    /// mouse the way every other window does and closes on the same key.
    /// </summary>
    public override bool PrefersUngrabbedMouse => true;

    public override bool TryOpen()
    {
        Compose();
        return base.TryOpen();
    }

    /// <summary>
    /// Lays the window out in one pass.
    ///
    /// Places a name field, the two pickers the game offers, and then the two
    /// answers that make this a Witchlight marker rather than a waypoint. Cancel
    /// and Save go last, at opposite ends of their row, where the game's own
    /// window puts them.
    /// </summary>
    private void Compose()
    {
        var label = ElementBounds.Fixed(0, 28, 100, 25);
        var field = label.RightCopy();
        var row = ElementBounds.Fixed(0, 28, 360, 25);
        // The switches' own column, down the left edge the pickers above them
        // start at. The box is placed first and the words follow it. Where the
        // column starts is settled below, once the picture picker reports how tall
        // it came out.
        var toggle = ElementBounds.Fixed(0, 28, SwitchWidth, SwitchSize);

        var inside = ElementBounds.Fill.WithFixedPadding(GuiStyle.ElementToDialogPadding);
        inside.BothSizing = ElementSizing.FitToChildren;
        inside.WithChildren(label, field);

        SingleComposer?.Dispose();
        SingleComposer = capi.Gui
            .CreateCompo(Composed, ElementStdBounds.AutosizedMainDialog
                .WithAlignment(EnumDialogArea.RightMiddle)
                .WithFixedAlignmentOffset(-GuiStyle.DialogToScreenPadding, 0))
            .AddShadedDialogBG(inside, false)
            .AddDialogTitleBar($"Witchlight marker — {Where()}", () => TryClose())
            .BeginChildElements(inside)

            .AddStaticText("Name", CairoFont.WhiteSmallText(), label = label.FlatCopy())
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
                label = label.BelowCopy(0, 5).WithFixedSize(27, 27), 270, PicturePicker)

            // The two answers the game's own window has no idea about, labelled in
            // words rather than with an emblem.
            //
            // The switch sits to the left of its label in both rows, and the two
            // switches share a column. A reader finds a control by its box and then
            // reads it, so boxes on the right would make the eye cross a line of
            // text of unpredictable length to find the answer.
            .AddSwitch(on => _private = on,
                toggle = Under(label), PrivateSwitch, SwitchSize, SwitchPad)
            .AddStaticText("Private", CairoFont.WhiteSmallText(),
                label = Beside(toggle))

            .AddSwitch(on => _preset = on,
                toggle = toggle.BelowCopy(0, 6), PresetSwitch, SwitchSize, SwitchPad)
            // Label the switch with what it does rather than the block code it is
            // keyed on. The preset carries the pattern, and "keep as what
            // game:rock-granite-* starts as" is a sentence nobody reads to the end
            // of.
            .AddStaticText("Set as preset", CairoFont.WhiteSmallText(),
                label = Beside(toggle))

            // Level with each other under the last switch, one at each end of the
            // row, where the game's own window puts them. Positioned from the
            // switch column rather than the labels, so they line up with the boxes.
            .AddSmallButton("Cancel", OnCancel,
                row.FlatCopy().FixedUnder(toggle, 30).WithFixedWidth(100),
                EnumButtonStyle.Normal)
            // Between Cancel and Save. It fills the window in rather than leaving
            // it, so it is neither.
            .AddSmallButton("Presets", OnPresets,
                row.FlatCopy().FixedUnder(toggle, 30).WithFixedWidth(100)
                    .WithAlignment(EnumDialogArea.CenterFixed),
                EnumButtonStyle.Normal)
            .AddSmallButton("Save", OnSave,
                row.FlatCopy().FixedUnder(toggle, 30).WithFixedWidth(100)
                    .WithAlignment(EnumDialogArea.RightFixed),
                EnumButtonStyle.Normal)
            .EndChildElements()
            .Compose();

        SingleComposer.GetTextInput(NameInput).SetValue(_offer.Title);
        SingleComposer.GetSwitch(PrivateSwitch).SetValue(_private);
        SingleComposer.GetSwitch(PresetSwitch).SetValue(_preset);
        SingleComposer.ColorListPickerSetValue(ColourPicker, Chosen(_colour));
        SingleComposer.IconListPickerSetValue(PicturePicker, Array.IndexOf(_pictures, _picture));
    }

    /// <summary>The game's own switch height, padding and column width. Stated
    ///  once so the two switch rows cannot come out at different heights.</summary>
    private const int SwitchSize = 30;
    private const int SwitchPad = 4;
    private const int SwitchWidth = 40;

    /// <summary>
    /// Returns where the first switch goes, under whatever height the picture
    /// picker came out at. A picker wraps to as many rows as the mods on the server
    /// give it, so nothing here may assume its height.
    /// </summary>
    private static ElementBounds Under(ElementBounds pictures) =>
        ElementBounds.Fixed(
            0,
            // Four pixels up, so the switch and its label read as one row. A
            // switch is taller than a line of text.
            pictures.fixedY + pictures.fixedHeight * 2 + 9 - 4,
            SwitchWidth,
            SwitchSize);

    /// <summary>Returns the bounds for a switch's label, level with the line of
    ///  text the switch is centred on and clear of the box by this window's
    ///  standard gap.</summary>
    private static ElementBounds Beside(ElementBounds toggle) =>
        ElementBounds.Fixed(toggle.fixedX + SwitchWidth + 6, toggle.fixedY + 4, 280, 25);

    /// <summary>Where the marker goes, in world coordinates.</summary>
    private string Where() =>
        $"{Blocks.At(_offer.X)}, {Blocks.At(_offer.Y)}, {Blocks.At(_offer.Z)}";

    /// <summary>Returns the swatch matching the colour the server offered, or the
    ///  first swatch.</summary>
    private int Chosen(string colour)
    {
        var packed = Markers.Packed(colour);
        if (packed is null)
        {
            return 0;
        }
        var at = Array.IndexOf(_colours, packed.Value);
        return at < 0 ? 0 : at;
    }

    /// <summary>Returns the picture the server offered when the game has one by
    ///  that name, and the first otherwise. A preset naming a picture from a mod
    ///  since removed must not stop the window opening.</summary>
    private static string Offered(string[] offered, string wanted, string instead)
    {
        if (offered.Length == 0)
        {
            return instead;
        }
        return Array.IndexOf(offered, wanted) >= 0 ? wanted
            : Array.IndexOf(offered, instead) >= 0 ? instead
            : offered[0];
    }

    private void OnColour(int index)
    {
        _colour = Markers.Hex(_colours[GuiElements.Within(index, _colours.Length)]);
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

    /// <summary>The preset list's layout: the row height, how many rows show at
    ///  once, the table width, and where the block column starts.</summary>
    private const int RowHeight = 28;
    private const int RowsShown = 8;
    private const int ListWidth = 440;
    private const int NameWidth = 150;
    private const int CellPad = 6;

    /// <summary>What was typed into the search box, lowercased once.</summary>
    private string _finding = "";

    /// <summary>Which column the rows are sorted by, and in which direction.</summary>
    private bool _byBlock;
    private bool _descending;

    /// <summary>True while the preset list is on the screen.</summary>
    private bool Listing => Composers[PresetsComposed] is { Enabled: true };

    /// <summary>Shows the preset list or hides it. The one button does both.</summary>
    private bool OnPresets()
    {
        if (Listing)
        {
            HidePresets();
        }
        else
        {
            ShowPresets();
        }
        return true;
    }

    /// <summary>
    /// Returns the presets the table shows: the ones the search matches, in the
    /// order the headers say.
    ///
    /// Filters and sorts in one function, so what is on the screen is the answer to
    /// one call and a test can ask it without a screen.
    /// </summary>
    public static List<PresetOffer> Shown(
        IEnumerable<PresetOffer> offered, string finding, bool byBlock, bool descending)
    {
        var wanted = finding.Trim().ToLowerInvariant();
        var rows = offered
            .Where(preset => wanted.Length == 0
                || preset.Title.ToLowerInvariant().Contains(wanted)
                || preset.Pattern.ToLowerInvariant().Contains(wanted));
        var ordered = byBlock
            ? rows.OrderBy(preset => preset.Pattern, StringComparer.OrdinalIgnoreCase)
                .ThenBy(preset => preset.Title, StringComparer.OrdinalIgnoreCase)
            : rows.OrderBy(preset => preset.Title, StringComparer.OrdinalIgnoreCase)
                .ThenBy(preset => preset.Pattern, StringComparer.OrdinalIgnoreCase);
        return (descending ? ordered.Reverse() : ordered).ToList();
    }

    /// <summary>
    /// Lays the preset table out to the left of the window.
    ///
    /// Builds it as another composer under this dialog rather than a dialog of its
    /// own, so it opens and closes with the window and takes the mouse the same
    /// way. Measures the window to place the table against its edge, because the
    /// window is as wide as its pickers came out and that depends on how many
    /// pictures the server has.
    ///
    /// Draws a search box over two headed columns, name and block. Puts the rows in
    /// one container inside a clipped inset, which is the shape the game's own mod
    /// list takes. The container owns every cell's bounds, so scrolling is moving
    /// the container, and the clip stops rows past the bottom drawing over the
    /// window. Shades every other row, cuts each cell's left-aligned text to its
    /// column, and puts a faceless button behind the cells so a press anywhere on
    /// the row picks it.
    ///
    /// Typing or pressing a header lays the whole table out again. The rows are
    /// cheap, and a table that patches itself can disagree with its own headers.
    /// </summary>
    private void ShowPresets()
    {
        Composers[PresetsComposed]?.Dispose();

        var shown = Shown(_offer.Presets, _finding, _byBlock, _descending);
        var find = ElementBounds.Fixed(0, 28, ListWidth, 26);
        var nameHead = ElementBounds.Fixed(0, 62, NameWidth, 24);
        var blockHead = ElementBounds.Fixed(NameWidth + CellPad, 62, ListWidth - NameWidth - CellPad, 24);
        var inset = ElementBounds.Fixed(0, 92, ListWidth, RowsShown * RowHeight + 6);
        var clip = inset.ForkContainingChild(3, 3, 3, 3);
        var rows = clip.ForkContainingChild(0, 0, 0, -3);
        rows.fixedHeight = Math.Max(1, shown.Count) * RowHeight;
        var scroll = ElementStdBounds.VerticalScrollbar(inset);

        var inside = ElementBounds.Fill.WithFixedPadding(GuiStyle.ElementToDialogPadding);
        inside.BothSizing = ElementSizing.FitToChildren;
        inside.WithChildren(find, nameHead, blockHead, inset, scroll);

        var beside = ElementStdBounds.AutosizedMainDialog
            .WithAlignment(EnumDialogArea.RightMiddle)
            .WithFixedAlignmentOffset(-(GuiStyle.DialogToScreenPadding + WindowWidth() + 8), 0);

        var composer = capi.Gui
            .CreateCompo(PresetsComposed, beside)
            .AddShadedDialogBG(inside, false)
            .AddDialogTitleBar("Presets", HidePresets)
            .BeginChildElements(inside)
            .AddTextInput(find, OnFinding, CairoFont.TextInput(), PresetsFind)
            .AddSmallButton(Headed("Name", byBlock: false), () => Reorder(byBlock: false), nameHead, EnumButtonStyle.Small)
            .AddSmallButton(Headed("Block", byBlock: true), () => Reorder(byBlock: true), blockHead, EnumButtonStyle.Small)
            .AddInset(inset, 3)
            .AddVerticalScrollbar(OnPresetsScrolled, scroll, PresetsScrollbar)
            .BeginClip(clip)
            .AddContainer(rows, PresetsRows)
            .EndClip()
            .EndChildElements();

        var table = composer.GetContainer(PresetsRows);
        var cell = CairoFont.WhiteSmallText().WithOrientation(EnumTextOrientation.Left);
        var blockWidth = ListWidth - NameWidth - CellPad * 3 - 6;

        if (shown.Count == 0)
        {
            table.Add(new GuiElementStaticText(capi,
                _offer.Presets.Count == 0
                    ? "Nothing kept yet. Tick \"Set as preset\" here, or make one on the map."
                    : "Nothing matches.",
                EnumTextOrientation.Left,
                ElementBounds.Fixed(CellPad, 4, ListWidth - CellPad * 2, RowHeight * 2),
                cell));
        }

        for (var at = 0; at < shown.Count; at++)
        {
            var preset = shown[at];
            var top = at * RowHeight;
            if (at % 2 == 1)
            {
                table.Add(new GuiElementCustomDraw(capi,
                    ElementBounds.Fixed(0, top, ListWidth, RowHeight), Stripe));
            }
            table.Add(new GuiElementTextButton(capi, "", cell, cell, () => Pick(preset),
                ElementBounds.Fixed(0, top, ListWidth, RowHeight), EnumButtonStyle.None));
            table.Add(new GuiElementStaticText(capi,
                Fitted(cell, preset.Title, NameWidth - CellPad),
                EnumTextOrientation.Left,
                ElementBounds.Fixed(CellPad, top + 5, NameWidth - CellPad, RowHeight - 6),
                cell));
            table.Add(new GuiElementStaticText(capi,
                Fitted(cell, preset.Pattern, blockWidth),
                EnumTextOrientation.Left,
                ElementBounds.Fixed(NameWidth + CellPad, top + 5, blockWidth, RowHeight - 6),
                cell));
        }

        composer.Compose();
        rows.CalcWorldBounds();
        clip.CalcWorldBounds();
        composer.GetScrollbar(PresetsScrollbar)
            .SetHeights((float)clip.fixedHeight, (float)rows.fixedHeight);
        var box = composer.GetTextInput(PresetsFind);
        box.SetPlaceHolderText("find a preset");
        if (_finding.Length > 0)
        {
            box.SetValue(_finding);
        }

        _presetRows = rows;
        Composers[PresetsComposed] = composer;
    }

    /// <summary>The shading on every other row, so the eye keeps its line.</summary>
    private static void Stripe(Cairo.Context ctx, Cairo.ImageSurface surface, ElementBounds bounds)
    {
        ctx.SetSourceRGBA(1, 1, 1, 0.06);
        ctx.Rectangle(bounds.drawX, bounds.drawY, bounds.InnerWidth, bounds.InnerHeight);
        ctx.Fill();
    }

    /// <summary>
    /// Returns the text, or as much of it as its column holds with an ellipsis
    /// after.
    ///
    /// Measures in the font it is drawn in, against the column's unscaled width
    /// brought up to the screen's scale, so what fits at one interface size fits at
    /// another.
    /// </summary>
    private static string Fitted(CairoFont font, string text, double width)
    {
        var room = width * RuntimeEnv.GUIScale;
        if (font.GetTextExtents(text).Width <= room)
        {
            return text;
        }
        var kept = text;
        while (kept.Length > 1 && font.GetTextExtents(kept + "…").Width > room)
        {
            kept = kept[..^1];
        }
        return kept.TrimEnd() + "…";
    }

    /// <summary>Returns a header's text, with an arrow on the column the rows are
    ///  sorted by.</summary>
    private string Headed(string name, bool byBlock) =>
        byBlock == _byBlock ? name + (_descending ? " ▼" : " ▲") : name;

    /// <summary>Sorts by the column pressed, or reverses it when already sorted by
    ///  that column.</summary>
    private bool Reorder(bool byBlock)
    {
        _descending = byBlock == _byBlock && !_descending;
        _byBlock = byBlock;
        ShowPresets();
        return true;
    }

    /// <summary>
    /// Lays the rows out again as the search changes. Rebuilds the search box with
    /// the table and puts the text back into it, so typing is not interrupted.
    /// </summary>
    private void OnFinding(string typed)
    {
        if (typed == _finding)
        {
            return;
        }
        _finding = typed;
        ShowPresets();
        Composers[PresetsComposed]?.FocusElement(
            Composers[PresetsComposed]!.GetTextInput(PresetsFind).TabIndex);
    }

    /// <summary>Returns the window's width on screen, in the unscaled units a fixed
    ///  offset is given in.</summary>
    private double WindowWidth() =>
        SingleComposer.Bounds.OuterWidth / RuntimeEnv.GUIScale;

    private void OnPresetsScrolled(float value)
    {
        if (_presetRows is null)
        {
            return;
        }
        _presetRows.fixedY = -value;
        _presetRows.CalcWorldBounds();
    }

    private void HidePresets()
    {
        if (Composers[PresetsComposed] is { } list)
        {
            list.Enabled = false;
        }
    }

    /// <summary>
    /// Fills the window from a preset, the way the marking key would have from its
    /// block.
    ///
    /// Takes every answer the preset gives. Leaves the window's current value for
    /// an answer the preset does not give, such as a colour the game no longer
    /// offers or a visibility nobody set. That value is the server's default for
    /// this marker.
    /// </summary>
    private bool Pick(PresetOffer preset)
    {
        if (preset.Title.Length > 0)
        {
            SingleComposer.GetTextInput(NameInput).SetValue(preset.Title);
        }
        if (Array.IndexOf(_pictures, preset.Icon) >= 0)
        {
            _picture = preset.Icon;
            SingleComposer.IconListPickerSetValue(PicturePicker, Array.IndexOf(_pictures, _picture));
        }
        if (Markers.Packed(preset.Color) is { } packed && Array.IndexOf(_colours, packed) >= 0)
        {
            _colour = Markers.Hex(packed);
            SingleComposer.ColorListPickerSetValue(ColourPicker, Chosen(_colour));
        }
        if (preset.Private != Mark.Unsaid)
        {
            _private = preset.Private == Mark.Private;
            SingleComposer.GetSwitch(PrivateSwitch).SetValue(_private);
        }
        HidePresets();
        return true;
    }

    /// <summary>Closes the preset list with the window, however the window closed.</summary>
    public override void OnGuiClosed()
    {
        Composers[PresetsComposed]?.Dispose();
        _presetRows = null;
        base.OnGuiClosed();
    }

    /// <summary>
    /// Sends what the window is holding and closes it.
    ///
    /// Creates nothing locally. The waypoint lives on the server and the preset
    /// lives on the map service, so this asks and closes, and the result arrives as
    /// a line of chat.
    /// </summary>
    private bool OnSave()
    {
        _send(new MarkAsk
        {
            X = _offer.X,
            Y = _offer.Y,
            Z = _offer.Z,
            BlockX = _offer.BlockX,
            BlockY = _offer.BlockY,
            BlockZ = _offer.BlockZ,
            // This window asked every question a preset would have answered, so a
            // preset must not answer them again over the top.
            UsePreset = false,
            Title = SingleComposer.GetTextInput(NameInput).GetText(),
            Icon = _picture,
            Color = _colour,
            Private = Mark.Says(_private),
            KeepPreset = _preset,
            Pattern = _offer.Pattern,
        });
        TryClose();
        return true;
    }
}

/// <summary>
/// Bounds a list picker's index before it is used.
///
/// The game hands back whatever was clicked, and a picker whose list changed under
/// it can hand back an index past the end. Both pickers call this.
/// </summary>
public static class GuiElements
{
    public static int Within(int index, int many) => index < 0 || index >= many ? 0 : index;
}
