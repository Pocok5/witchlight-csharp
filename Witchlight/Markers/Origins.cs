using System.Collections.Generic;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace Witchlight;

/// <summary>
/// Stores which block each marker was put on.
///
/// A waypoint is a place and nothing else, and nothing in it says what was
/// standing there. Presets are this mod's idea and are keyed on a block, so only
/// the mod can answer this, and only at the moment the marker is made while the
/// chunk is loaded. A marker made on an ore vein since mined out would otherwise
/// report whatever is there now.
///
/// So the block is read once and kept in the savegame beside the waypoints,
/// through <see cref="Beside{T}"/>, the same store <see cref="Visibility"/> uses.
///
/// Stores the block code rather than its name. A code is the block's identity and
/// is what a preset's pattern matches against, while a name is words in the
/// reader's language and two servers would disagree about it.
///
/// The stored value can also be a pattern rather than a code. Making a screenful
/// of markers look like a preset sets this to that preset's pattern, which is the
/// preset saying what kind of thing those markers are.
/// </summary>
public sealed class Origins
{
    private const string SaveKey = "witchlight:markerblocks";
    private const string Called = "the blocks markers were made on";

    private readonly Beside<string> _made;

    private Origins(Beside<string> made)
    {
        _made = made;
    }

    /// <summary>An empty store, matching every marker made before this was kept.
    ///  The mod holds this before the world is up.</summary>
    public static Origins Empty => new(Beside<string>.Empty(SaveKey));

    /// <summary>How many markers this knows the block of. Reported by status.</summary>
    public int Known => _made.Count;

    /// <summary>Reads back what a previous run stored.</summary>
    public static Origins Read(ICoreServerAPI api) =>
        new(Beside<string>.Read(api, SaveKey, Called));

    /// <summary>Writes the blocks, when they are not the ones already stored.</summary>
    public void Write(ICoreServerAPI api, IEnumerable<Waypoint>? alive) =>
        _made.Write(api, alive, Called);

    /// <summary>Returns the block one marker was put on, or null when it was made
    ///  before the mod kept the answer.</summary>
    public string Of(string? guid) => _made.Knows(guid, out var code) ? code : "";

    /// <summary>
    /// Records which block a marker is about.
    ///
    /// Recorded again on every change, so a marker that has moved is about whatever
    /// is under it now. A request that names a block itself overrides that, which
    /// is the map saying these markers belong to a preset whatever they stand on.
    /// <see cref="PendingMarkers"/> decides between the two.
    /// </summary>
    public void Made(string? guid, string? code)
    {
        if (string.IsNullOrEmpty(code))
        {
            return;
        }
        _made.Say(guid, code);
    }
}
