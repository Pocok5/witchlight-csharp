using System.Collections.Generic;
using Newtonsoft.Json;

namespace Witchlight;

/// <summary>
/// What a column may hold.
///
/// A closed set, because these names are written into the table the service
/// makes and the only safe way to put a plugin's word into SQL is to not put it
/// there. A plugin picks one of these; the service spells it.
/// </summary>
public enum PluginKind
{
    Int,
    Real,
    Text,
    Bool,
}

/// <summary>Who may see a plugin's rows.</summary>
public enum PluginScope
{
    /// <summary>Each row belongs to one player, who may share it with a group.</summary>
    Owner,

    /// <summary>Everybody's, like the terrain.</summary>
    World,
}

/// <summary>
/// What a plugin says its rows look like.
///
/// The whole of what a plugin tells the service about its storage. The service
/// makes the table from this, adds <c>owner_uid</c> itself and puts it at the
/// front of the key, and composes every statement — so a plugin describes its
/// own storage without ever being handed the ability to write SQL.
///
/// Declared once, in the plugin's <c>Start</c>, and passed to
/// <see cref="WitchlightPlugins.Register"/>. Registering again with the same
/// shape costs a lookup. Registering with a column added carries the rows
/// already there. Anything else that moved is refused with the data left alone,
/// and the plugin migrates it itself with
/// <see cref="WitchlightPlugins.Query"/> and
/// <see cref="WitchlightPlugins.StoreMany"/>.
/// </summary>
public sealed class PluginShape
{
    /// <summary>Every column, by name. Lowercase letters, digits, _ and - only.</summary>
    [JsonProperty("columns")]
    public Dictionary<string, string> Columns { get; } = new();

    /// <summary>
    /// Which columns identify a row, in the order they are given.
    ///
    /// <c>owner_uid</c> is added by the service and comes first; it is never
    /// named here. A row written again with the same key replaces the one
    /// already there.
    /// </summary>
    [JsonProperty("key")]
    public List<string> Key { get; } = new();

    /// <summary>
    /// Which columns a reader may ask ranges of, as <c>?x=-1000..1000</c>.
    ///
    /// The service builds the index that makes those cheap, and refuses a range
    /// asked of anything not named here — a range answered without an index is
    /// how a map goes quiet under load.
    /// </summary>
    [JsonProperty("ranged")]
    public List<string> Ranged { get; } = new();

    [JsonProperty("scope")]
    public string Scope { get; private set; } = "owner";

    /// <summary>Adds a column.</summary>
    public PluginShape Column(string name, PluginKind kind)
    {
        Columns[name] = kind switch
        {
            PluginKind.Int => "int",
            PluginKind.Real => "real",
            PluginKind.Bool => "bool",
            _ => "text",
        };
        return this;
    }

    /// <summary>Names the columns that identify a row.</summary>
    public PluginShape KeyedBy(params string[] columns)
    {
        Key.Clear();
        Key.AddRange(columns);
        return this;
    }

    /// <summary>Names the columns a range may be asked of.</summary>
    public PluginShape RangedBy(params string[] columns)
    {
        Ranged.Clear();
        Ranged.AddRange(columns);
        return this;
    }

    /// <summary>Says who may see these rows.</summary>
    public PluginShape SeenBy(PluginScope scope)
    {
        Scope = scope == PluginScope.World ? "world" : "owner";
        return this;
    }
}
