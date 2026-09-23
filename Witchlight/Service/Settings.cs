using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace Witchlight;

/// <summary>
/// Reads the settings file that both the mod and the map service use.
///
/// The map service writes the file and owns its format. This class looks up the
/// handful of values the mod needs rather than parsing the whole format, so
/// there is only one full parser to keep in step with the format.
///
/// Every setting the mod reads comes through here, so each setting has one
/// default in one place.
/// </summary>
public static class Settings
{
    /// <summary>The path of the settings file, beside the server's other mod settings.</summary>
    public static string Path =>
        System.IO.Path.GetFullPath(System.IO.Path.Combine(GamePaths.ModConfig, "witchlight.conf"));

    /// <summary>
    /// The root directory for map data, before any per-world directory inside it.
    ///
    /// Defaults to the `witchlight` folder beside the world data. The `map_data`
    /// setting overrides it.
    /// </summary>
    public static string MapData
    {
        get
        {
            var told = Value("map_data");
            return string.IsNullOrWhiteSpace(told)
                ? System.IO.Path.Combine(GamePaths.DataPath, ExportDirName)
                : told.Trim();
        }
    }

    /// <summary>
    /// True when each world's map goes in a directory of its own.
    ///
    /// <see cref="ForWorld"/> sets it once the world is up.
    /// </summary>
    public static bool PerWorld { get; private set; }

    /// <summary>
    /// The directory every export lands in.
    ///
    /// Both halves use this one directory. It is <see cref="MapData"/> until
    /// <see cref="ForWorld"/> settles the per-world directory.
    /// </summary>
    public static string Exports => _exports ?? MapData;

    /// <summary>The settled per-world export directory, or null before a world loads.</summary>
    private static string? _exports;

    /// <summary>
    /// Settles which directory this world's map goes in.
    ///
    /// Called once the world is up, since the directory is named after the world.
    /// The palette, the block names and the icons export after this call rather
    /// than when the assets finish loading.
    /// </summary>
    public static void ForWorld(ICoreServerAPI api)
    {
        // An absent setting means on. Every singleplayer save shares one data
        // path, so with it off a save would write into the last world's map.
        PerWorld = On("per_world", byDefault: true);
        _exports = MapDirectory.Settle(api, MapData, PerWorld);
        Directory.CreateDirectory(_exports);
    }

    private const string ExportDirName = "witchlight";

    /// <summary>True when the mod runs the map service itself.</summary>
    public static bool Autostarts => On("autostart", byDefault: true);

    /// <summary>True when a joining player is told the map's address.</summary>
    public static bool Announces => On("announce", byDefault: true);

    /// <summary>
    /// When true, a new marker whose owner has not chosen a visibility is visible
    /// to everyone. When false, only its owner sees it. Default false. The map
    /// service reads the same setting, so the in-game map and the web map agree.
    /// </summary>
    public static bool MarkersPublic => On("allow_public_markers", byDefault: false);

    /// <summary>The negation of <see cref="MarkersPublic"/>.</summary>
    public static bool MarkersPrivateByDefault => !MarkersPublic;

    /// <summary>
    /// When true, any signed-in player may edit a public marker. When false, only
    /// the owner may edit it. A private marker can only ever be edited by its
    /// owner. Default false.
    /// </summary>
    public static bool PublicMarkersEditable => On("allow_editing_public_markers", byDefault: false);

    /// <summary>
    /// When true, every player's position is sent to every viewer. When false, a
    /// player's position is sent only to members of their own group. Default
    /// true. <see cref="PrivateMap"/> overrides it: while personal maps are on,
    /// positions are always restricted to the player's own group. The mod
    /// enforces this, because the mod is the half that knows the groups.
    /// </summary>
    public static bool PlayersPublic => On("show_players_to_everyone", byDefault: true) && !PrivateMap;

    /// <summary>
    /// When true, each player sees only the terrain they have explored. The map
    /// service draws the map that way; the mod's part is to restrict each
    /// player's position to their own group and to tell the service who is in
    /// which group. Default true.
    /// </summary>
    public static bool PrivateMap => On("personal_maps", byDefault: true);

    /// <summary>
    /// True when the map draws the claims worldgen made around trader camps and
    /// story structures. Default false.
    ///
    /// The game stores these as ordinary land claims, so <see cref="ClaimFeed"/>
    /// reads them from the same list. They exist from the moment the ground
    /// generated rather than from the moment a player found them, so drawing them
    /// tells every viewer where every trader is.
    ///
    /// The mod enforces the setting by leaving these claims out of what it sends,
    /// never by having the page decline to draw them. A claim that reached a
    /// browser can be read out of it.
    /// </summary>
    public static bool ClaimsWorldgen => On("claims.worldgen", byDefault: false);

    /// <summary>
    /// The gap between terrain exports, in milliseconds. Default 10000.
    ///
    /// Every change a chunk makes within one interval is written once. Raising
    /// the value trades how current the terrain is against how often the disk is
    /// touched. A world save exports whatever the interval was holding, so a
    /// change is delayed rather than lost.
    ///
    /// Clamped between 1000 and 600000. An export runs on the server's own tick,
    /// so a smaller gap spends server time on the map instead of the world. The
    /// note above the line in the settings file states the same two bounds.
    /// </summary>
    public static int ExportIntervalMs =>
        Math.Clamp(Number("export_interval_ms", byDefault: 10000), 1000, 600000);

    /// <summary>
    /// Returns the map address to give a player, or null when there is none.
    ///
    /// Returns the `announce_url` setting when the operator set one, and
    /// otherwise <see cref="Address"/>. A server behind a proxy answers at a name
    /// and port the service never sees, so the address the service works out is
    /// right only on a machine a player can reach directly.
    /// </summary>
    public static string? Announcement()
    {
        var told = Value("announce_url");
        return string.IsNullOrWhiteSpace(told) ? Address() : told.Trim();
    }

    /// <summary>The path of the file the service writes its addresses to.</summary>
    public static string AddressPath => System.IO.Path.Combine(Exports, "service.json");

    /// <summary>
    /// Returns the address the map last published, or null when there is none.
    ///
    /// The service resolves its bind address into the addresses it actually
    /// answers on, since a bind address such as `0.0.0.0` is not reachable, and
    /// writes them in preference order. This returns the first.
    /// </summary>
    public static string? Address()
    {
        try
        {
            var path = AddressPath;
            if (!File.Exists(path))
            {
                return null;
            }

            return JObject.Parse(File.ReadAllText(path))["Urls"] is JArray { Count: > 0 } urls
                ? urls[0].ToString()
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Creates the settings file if it is missing, by running the service with
    /// `--save-config`. Returns its path, or null when it could not be written.
    ///
    /// The service writes the file because the service owns the format. The data
    /// path is passed in because the service cannot work it out for itself.
    /// </summary>
    public static string? EnsureWritten(ICoreServerAPI api, string executable)
    {
        var path = Path;
        if (File.Exists(path))
        {
            return path;
        }

        try
        {
            Directory.CreateDirectory(GamePaths.ModConfig);

            var write = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            write.ArgumentList.Add("--config");
            write.ArgumentList.Add(path);
            write.ArgumentList.Add("--vs-data");
            write.ArgumentList.Add(GamePaths.DataPath);
            // Written once; after that it is the operator's to change.
            write.ArgumentList.Add("--per-world");
            write.ArgumentList.Add("true");
            write.ArgumentList.Add("--save-config");
            write.ArgumentList.Add("--print-config");

            using var writing = Process.Start(write);
            if (writing is null)
            {
                return null;
            }

            writing.StandardOutput.ReadToEnd();
            var complaint = writing.StandardError.ReadToEnd();
            writing.WaitForExit(WriteConfigMs);

            if (!File.Exists(path))
            {
                api.Logger.Warning(
                    "[witchlight] the map service did not write {0}: {1}", path, complaint.Trim());
                return null;
            }

            api.Logger.Notification("[witchlight] wrote default map settings to {0}", path);
            return path;
        }
        catch (Exception error)
        {
            api.Logger.Error("[witchlight] could not write {0}: {1}", path, error);
            return null;
        }
    }

    /// <summary>How long to wait for the service to write the file, in milliseconds.
    /// Long enough for a cold start on a slow disk.</summary>
    private const int WriteConfigMs = 15000;

    /// <summary>
    /// Returns one setting's value by name, or null when the file does not set it.
    ///
    /// Name a setting inside a table by its whole name, such as `commands.export`.
    /// Walks <see cref="Lines"/>.
    /// </summary>
    public static string? Value(string key)
    {
        // The whole key, not a prefix of one: a setting named `announce_url`
        // must not answer for `announce`.
        foreach (var (table, name, said) in Settings.Lines())
        {
            if (name is not null && (table + name).Equals(key, StringComparison.Ordinal))
            {
                return said;
            }
        }

        return null;
    }

    /// <summary>
    /// Returns true when the file has this table header, even with no settings
    /// under it. Walks <see cref="Lines"/>.
    ///
    /// An absent table means the defaults, and an empty table means the operator
    /// wants none of it. <see cref="Table"/> returns nothing in both cases, so
    /// callers that need to tell them apart ask this first.
    /// </summary>
    public static bool HasTable(string table)
    {
        var wanted = table + ".";
        foreach (var (named, _, _) in Settings.Lines())
        {
            if (named.Equals(wanted, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Returns every setting inside one table, in the order the file gives them.
    /// Walks <see cref="Lines"/>.
    ///
    /// <see cref="Value"/> serves settings with known names. This serves a table
    /// whose keys the operator chooses, such as the bars a player's card carries,
    /// where the file order is the draw order.
    /// </summary>
    public static IEnumerable<(string Key, string Said)> Table(string table)
    {
        var wanted = table + ".";
        var found = new List<(string, string)>();
        foreach (var (named, name, said) in Settings.Lines())
        {
            if (name is not null && named.Equals(wanted, StringComparison.Ordinal))
            {
                found.Add((name, said!));
            }
        }

        return found;
    }

    /// <summary>
    /// Yields every meaningful line of the settings file, in order. The one place
    /// that knows how a line is shaped, and the walker behind
    /// <see cref="Value"/>, <see cref="HasTable"/> and <see cref="Table"/>.
    ///
    /// Comment lines are skipped. Each result carries the table the line sits
    /// under, written with its trailing dot so a key matches by its whole name. A
    /// table header yields its own name with a null key. A setting yields its key
    /// and value. An unreadable file yields nothing.
    /// </summary>
    private static IEnumerable<(string Table, string? Key, string? Said)> Lines()
    {
        List<string> read;
        try
        {
            read = new List<string>(File.ReadLines(Path));
        }
        catch (Exception)
        {
            yield break;
        }

        var table = "";
        foreach (var line in read)
        {
            var text = line.Trim();
            if (text.StartsWith('#'))
            {
                continue;
            }

            if (text.StartsWith('[') && text.EndsWith(']'))
            {
                table = text[1..^1].Trim() + ".";
                yield return (table, null, null);
                continue;
            }

            var at = text.IndexOf('=');
            if (at > 0)
            {
                yield return (table, text[..at].Trim(), Said(text[(at + 1)..]));
            }
        }
    }

    /// <summary>
    /// Returns the value on the right of an equals sign.
    ///
    /// A quoted value ends at its closing quote. An unquoted one ends at a
    /// comment, so `announce = false # off` reads as `false`.
    /// </summary>
    private static string Said(string after)
    {
        var text = after.Trim();
        if (text.StartsWith('"'))
        {
            var close = text.IndexOf('"', 1);
            return close < 0 ? text[1..] : text[1..close];
        }

        var comment = text.IndexOf('#');
        return (comment < 0 ? text : text[..comment]).Trim();
    }

    /// <summary>
    /// Returns a numeric setting, or <paramref name="byDefault"/> when the file
    /// does not set it or the value is not a number.
    ///
    /// A value that is not a number falls back to the default rather than zero,
    /// so a typo does not read as a request for the fastest possible interval.
    /// </summary>
    /// <param name="key">The setting's whole name.</param>
    /// <param name="byDefault">The value to use when the file does not say.</param>
    private static int Number(string key, int byDefault)
    {
        var said = Value(key);
        return int.TryParse(said?.Trim(), out var number) ? number : byDefault;
    }

    /// <summary>
    /// Returns a true-or-false setting, or <paramref name="byDefault"/> when the
    /// file does not set it.
    ///
    /// Each caller passes its own default, because these settings do not all lean
    /// the same way. A map announces itself unless told not to, and shares no
    /// markers unless told to.
    /// </summary>
    /// <param name="key">The setting's whole name.</param>
    /// <param name="byDefault">The value to use when the file does not say.</param>
    private static bool On(string key, bool byDefault)
    {
        var said = Value(key);
        return string.IsNullOrWhiteSpace(said)
            ? byDefault
            : string.Equals(said.Trim(), "true", StringComparison.OrdinalIgnoreCase);
    }
}
