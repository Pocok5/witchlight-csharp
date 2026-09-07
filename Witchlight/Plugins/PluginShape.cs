using System.Collections.Generic;
using Newtonsoft.Json;

namespace Witchlight;

/// <summary>
/// The types a plugin's column may hold.
///
/// A closed set. These names go into the table the service creates, and a plugin
/// picks one rather than supplying a word that would reach SQL.
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

    /// <summary>Every row is visible to everybody, like the terrain.</summary>
    World,
}

/// <summary>
/// The shape a plugin declares for its rows.
///
/// This is everything a plugin tells the service about its storage. The service
/// creates the table from it, adds <c>owner_uid</c> itself at the front of the
/// key, and composes every statement, so a plugin describes its storage without
/// writing SQL.
///
/// Declared once in the plugin's <c>Start</c> and passed to
/// <see cref="WitchlightPlugins.Register"/>. Registering again with the same
/// shape costs a lookup. Registering with a column added keeps the rows already
/// there. Any other change is refused with the data left alone, and the plugin
/// migrates it itself with <see cref="WitchlightPlugins.Query"/> and
/// <see cref="WitchlightPlugins.StoreMany"/>.
/// </summary>
public sealed class PluginShape
{
    /// <summary>Every column, by name. Lowercase letters, digits, _ and - only.</summary>
    [JsonProperty("columns")]
    public Dictionary<string, string> Columns { get; } = new();

    /// <summary>
    /// The columns that identify a row, in the order they are given.
    ///
    /// The service adds <c>owner_uid</c> at the front, so never name it here. A
    /// row written again with the same key replaces the one already there.
    /// </summary>
    [JsonProperty("key")]
    public List<string> Key { get; } = new();

    /// <summary>
    /// The columns a reader may ask ranges of, as <c>?x=-1000..1000</c>.
    ///
    /// The service builds an index for each one and refuses a range asked of a
    /// column not named here. A range answered without an index makes a map go
    /// quiet under load.
    /// </summary>
    [JsonProperty("ranged")]
    public List<string> Ranged { get; } = new();

    [JsonProperty("scope")]
    public string Scope { get; private set; } = "owner";

    /// <summary>Adds a column and returns this shape.</summary>
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

    /// <summary>Sets the columns that identify a row and returns this shape.</summary>
    public PluginShape KeyedBy(params string[] columns)
    {
        Key.Clear();
        Key.AddRange(columns);
        return this;
    }

    /// <summary>Sets the columns a range may be asked of and returns this shape.</summary>
    public PluginShape RangedBy(params string[] columns)
    {
        Ranged.Clear();
        Ranged.AddRange(columns);
        return this;
    }

    /// <summary>Sets who may see these rows and returns this shape.</summary>
    public PluginShape SeenBy(PluginScope scope)
    {
        Scope = scope == PluginScope.World ? "world" : "owner";
        return this;
    }
}
