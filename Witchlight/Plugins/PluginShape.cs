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
/// What becomes of the rows already kept when a shape no longer fits them.
///
/// A column added is carried onto existing rows either way. This decides the
/// changes that cannot be carried — a column dropped, a key moved, a type or a
/// scope changed — where the plugin is the only one that knows whether what it
/// stored before still means anything.
/// </summary>
public enum PluginReshape
{
    /// <summary>
    /// Leaves the rows where they are and registers nothing.
    ///
    /// The default. A plugin whose old rows are worth keeping reads them with
    /// Query and writes them back with StoreMany under the new shape.
    /// </summary>
    Refuse,

    /// <summary>
    /// Keeps a copy, then builds the table again empty at the new shape.
    ///
    /// For a shape whose old rows measured something the new one does not, where
    /// there is nothing to carry forward and refusing leaves the plugin storing
    /// nothing at all. The copy is written before anything is dropped, and the
    /// service names it in the log.
    /// </summary>
    Rebuild,
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
/// <see cref="WitchlightPlugins.StoreMany"/> — or declares <see cref="Rebuilding"/>
/// to have the old rows set aside, where they measured something the new shape
/// does not and there is nothing to carry forward.
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

    /// <summary>
    /// What to do with rows already kept that this shape no longer fits.
    ///
    /// Sent to the service with the rest of the shape, but not part of what a
    /// shape *is*: it says how to get from one shape to the next, so changing
    /// only this is not itself a change to migrate.
    /// </summary>
    [JsonProperty("on_reshape")]
    public string OnReshape { get; private set; } = "refuse";

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

    /// <summary>
    /// Says the rows already kept may be set aside when this shape no longer
    /// fits them, and returns this shape.
    ///
    /// Declare this only where the old rows cannot be carried forward — where
    /// they measured something this shape does not, so that reading them with
    /// Query and writing them back with StoreMany would have nothing to write.
    /// The service keeps a copy beside the plugin's database before it drops
    /// anything and names it in the log, so this loses nothing irrecoverably;
    /// it does mean players stop seeing what they had.
    /// </summary>
    public PluginShape Rebuilding()
    {
        OnReshape = "rebuild";
        return this;
    }
}
