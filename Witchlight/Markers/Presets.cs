using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Vintagestory.API.Common;

namespace Witchlight;

/// <summary>
/// Reads what a marker starts as for the block a player is standing on.
///
/// The map service keeps a player's presets against their uid, and the map's own
/// form ordinarily makes one. This reads the same record from the game. The mod
/// asks the service for a player's presets at the moment they mark something,
/// picks the one that names the block, and hands the answer to their client.
///
/// Caches nothing between requests. A preset made in a browser a minute ago has to
/// apply to the next press of the key.
/// </summary>
public static class Presets
{
    /// <summary>
    /// Returns what one player has set for themselves, or an empty answer when the
    /// map service is not answering.
    ///
    /// Returns empty rather than null. A player who has never opened the map has
    /// set nothing, and a service that is down has said nothing. Both fall back to
    /// what the operator set.
    /// </summary>
    public static async Task<Person> Of(MapService service, string uid, ILogger log)
    {
        var body = await service.Presets(uid).ConfigureAwait(false);
        return Read(body, log) ?? new Person();
    }

    /// <summary>
    /// Saves one preset and returns everything that player has set once it lands.
    /// Returns null when the service would not take it.
    ///
    /// Logs the result either way. Nothing waits on this, because a marker that
    /// landed must not be undone by a service that would not take the preset beside
    /// it, so the log is the only place a failure can be reported.
    /// </summary>
    public static async Task<Person?> Keep(
        MapService service, string uid, Preset preset, ILogger log)
    {
        var body = await service.KeepPreset(uid, preset).ConfigureAwait(false);
        var kept = Read(body, log);
        if (kept is null)
        {
            log.Warning(
                "[witchlight] the map would not keep the preset for {0}", preset.Pattern);
        }
        else
        {
            log.Notification(
                "[witchlight] kept a preset for {0}; that player now has {1}",
                preset.Pattern, kept.Presets.Count);
        }
        return kept;
    }

    /// <summary>Returns the first preset whose pattern names this block, or null.</summary>
    public static Preset? For(Person person, string? code)
    {
        if (string.IsNullOrEmpty(code))
        {
            return null;
        }
        return person.Presets.FirstOrDefault(preset => BlockPattern.Fits(preset.Pattern, code));
    }

    private static Person? Read(string? body, ILogger log)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            return JsonConvert.DeserializeObject<Person>(body);
        }
        catch (Exception error)
        {
            log.Warning("[witchlight] could not read what the map holds for a player: {0}", error.Message);
            return null;
        }
    }
}

/// <summary>
/// One player's choices, in the shape the map service keeps them in.
///
/// The same document the map's own settings window reads and writes, so the two
/// sides cannot disagree about what somebody set. The service's `preferences.rs`
/// stores it and decides the field names.
/// </summary>
public class Person
{
    public List<Preset> Presets { get; set; } = new();

    /// <summary>
    /// True when a new marker of theirs is private. Absent when they have not
    /// decided, and the operator's <c>allow_public_markers</c> then decides.
    /// </summary>
    public bool? PrivateByDefault { get; set; }

    /// <summary>True when making a marker keeps it as a preset without asking.</summary>
    public bool PresetsByDefault { get; set; }
}

/// <summary>What a marker starts as for one kind of block.</summary>
public class Preset
{
    /// <summary>The block code this names. <c>*</c> stands for any run of
    /// characters. See <see cref="BlockPattern"/>.</summary>
    public string Pattern { get; set; } = "";

    public string Title { get; set; } = "";
    public string Icon { get; set; } = "";
    public string Color { get; set; } = "";

    /// <summary>True when markers made from this are their owner's alone. Absent
    /// when that player's own default decides.</summary>
    public bool? Private { get; set; }
}
