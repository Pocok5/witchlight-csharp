using System;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace Witchlight;

/// <summary>
/// Says whether one chunk's surface could be read, and why not where it could
/// not.
///
/// Each answer calls for a different fix. Unloaded means the server holds
/// nothing for the chunk, or holds a map chunk with no blocks under it. Both are
/// answered by asking the server for the chunk again. See <see cref="Repair"/>.
/// Unready means a chunk in memory whose height map is not built yet, which is
/// transient and answered by trying again next tick.
/// </summary>
public enum Readiness
{
    Ready,
    Unready,
    Unloaded,
}

/// <summary>Reads the surface out of loaded chunks.</summary>
public static class ColumnPump
{
    /// <summary>
    /// Reads one chunk's surface straight from the world. Returns null where it
    /// cannot be read right now.
    ///
    /// This asks the same two questions as a batch read, whether a map chunk is
    /// here and whether its blocks are here, and writes nothing afterwards. A
    /// terrain pull uses it when it wants this column's record regardless of what
    /// the map has stored.
    /// </summary>
    public static byte[]? ReadOne(
        ICoreServerAPI api, int chunkX, int chunkZ, System.Func<int, bool> shows, Microblocks chiselled)
    {
        var edge = api.WorldManager.ChunkSize;
        return TryRead(api, chunkX, chunkZ, shows, chiselled, new Surface(edge * edge), out var record)
            == Readiness.Ready
            ? record
            : null;
    }

    /// <summary>
    /// Returns where a chunk sits in the year, as the game reckons it, rounded to
    /// the month. Position matters, because the hemispheres are in opposite
    /// seasons.
    ///
    /// The rounding decides how often the map is redrawn. The year is stored as
    /// one byte, and at full precision that byte moves 255 times a year. Each
    /// step rewrites every region holding a column that crossed it, whether or
    /// not anybody has been near it. A month is the coarsest step the eye does not
    /// notice and the one the game counts in, and it cuts 255 redraws a year to
    /// twelve.
    ///
    /// The byte still means a position in the year from 0 to 255, which the map
    /// service reads as the coordinate to sample the season's colours at. Only
    /// the number of distinct values it takes is reduced.
    /// </summary>
    private static byte SeasonAt(ICoreServerAPI api, int chunkX, int chunkZ, int edge, BlockPos scratch)
    {
        scratch.Set(chunkX * edge + edge / 2, 0, chunkZ * edge + edge / 2);
        var calendar = api.World.Calendar;
        var season = calendar?.GetSeasonRel(scratch) ?? 0f;
        return InMonths(season, MonthsPerYear(calendar));
    }

    /// <summary>
    /// Returns the middle of the month a point in the year falls in, as the
    /// stored byte. The middle draws the month's own colour rather than the
    /// colour of the moment it began, and sits the same distance from wrong at
    /// both ends.
    /// </summary>
    private static byte InMonths(float season, int months)
    {
        // A year is a circle and `GetSeasonRel` may return the point where it
        // closes. That point belongs to the last month, not to a thirteenth.
        var round = Math.Clamp(season, 0f, 0.999999f);
        var month = (int)(round * months);
        var middle = (month + 0.5) / months;
        return (byte)Math.Clamp((int)(middle * 255.0), 0, 255);
    }

    /// <summary>
    /// Returns how many months the world's calendar divides its year into. A
    /// world may be configured with a longer month, so a hardcoded twelve would
    /// mean something else there.
    /// </summary>
    private static int MonthsPerYear(IGameCalendar? calendar)
    {
        if (calendar is null || calendar.DaysPerMonth <= 0 || calendar.DaysPerYear <= 0)
        {
            return DefaultMonthsPerYear;
        }

        var months = (int)Math.Round((double)calendar.DaysPerYear / calendar.DaysPerMonth);
        return Math.Clamp(months, 1, 255);
    }

    /// <summary>The months per year to assume when the calendar cannot be
    /// read.</summary>
    private const int DefaultMonthsPerYear = 12;

    /// <summary>
    /// Returns where the year has reached for each of these columns.
    ///
    /// This is arithmetic over positions the caller already holds, with no world
    /// lookup and no file read, so an idle server can ask whether it has anything
    /// to write without paying for an export.
    /// </summary>
    public static Dictionary<(int, int), byte> Seasons(
        ICoreServerAPI api,
        IEnumerable<(int, int)> columns,
        int edge)
    {
        var scratch = new BlockPos(0);
        var seasons = new Dictionary<(int, int), byte>();
        foreach (var (cx, cz) in columns)
        {
            seasons[(cx, cz)] = SeasonAt(api, cx, cz, edge, scratch);
        }
        return seasons;
    }

    /// <summary>
    /// Returns the columns whose season has moved.
    ///
    /// The result names columns rather than the regions holding them. A season
    /// lives in a region's directory, so a year advancing costs sixteen bytes for
    /// each chunk that crossed the step. Naming the region instead would repack
    /// every chunk in it, including the majority whose season had not moved.
    ///
    /// A step is a month. See <see cref="SeasonAt"/>.
    /// </summary>
    public static HashSet<(int, int)> SeasonsMoved(
        IReadOnlyDictionary<(int, int), byte> before,
        IReadOnlyDictionary<(int, int), byte> after)
    {
        var moved = new HashSet<(int, int)>();
        foreach (var (column, season) in after)
        {
            if (!before.TryGetValue(column, out var was) || was != season)
            {
                moved.Add(column);
            }
        }
        return moved;
    }

    /// <summary>
    /// Reads one chunk's surface and reports the reason where there is none to
    /// read.
    ///
    /// This asks three questions of one chunk: whether a map chunk is here,
    /// whether its height map is built, and whether its blocks are here. Asking
    /// per chunk lets the fast lane read a few per tick and report what it found
    /// for each.
    /// </summary>
    public static Readiness TryRead(
        ICoreServerAPI api,
        int chunkX,
        int chunkZ,
        System.Func<int, bool> shows,
        Microblocks chiselled,
        Surface surface,
        out byte[]? record)
    {
        record = null;
        var edge = api.WorldManager.ChunkSize;
        var mapChunk = api.WorldManager.GetMapChunk(chunkX, chunkZ);
        if (mapChunk is null)
        {
            return Readiness.Unloaded;
        }

        if (mapChunk.RainHeightMap is null)
        {
            return Readiness.Unready;
        }

        if (!Readable(api, chunkX, chunkZ, edge, mapChunk))
        {
            // The heightmap is here and the blocks are not, which reads as a
            // chunk of air rather than an absence. Asking the server for the
            // chunk answers both cases.
            return Readiness.Unloaded;
        }

        Read(api, chunkX, chunkZ, edge, mapChunk, surface, shows, chiselled);
        record = surface.Record();
        return Readiness.Ready;
    }

    /// <summary>
    /// Reads one column's entry, the six bytes the record holds for it, straight
    /// from the world. Returns null where its chunk cannot be read right now.
    ///
    /// This is what a block placed or broken costs: one column walked against the
    /// 1024 a whole chunk holds. The caller patches the chunk's record with the
    /// answer rather than reading the chunk again.
    /// </summary>
    public static byte[]? ReadColumn(
        ICoreServerAPI api, int x, int z, System.Func<int, bool> shows, Microblocks chiselled)
    {
        var edge = api.WorldManager.ChunkSize;
        var (chunkX, chunkZ) = ChunkOf(x, z, edge);
        var mapChunk = api.WorldManager.GetMapChunk(chunkX, chunkZ);
        if (mapChunk?.RainHeightMap is null || !Readable(api, chunkX, chunkZ, edge, mapChunk))
        {
            return null;
        }

        var index = Mod(z, edge) * edge + Mod(x, edge);
        var surface = new Surface(1);
        ReadAt(api, mapChunk, x, z, index, 0, surface, new BlockPos(0), shows, chiselled);
        return surface.Record();
    }

    /// <summary>Returns where a column sits in its chunk's record, as a byte offset.</summary>
    public static int OffsetOf(int x, int z, int edge) =>
        (Mod(z, edge) * edge + Mod(x, edge)) * Regions.EntryBytes;

    /// <summary>Returns the chunk a block is in. The division floors, as negative coordinates need.</summary>
    public static (int X, int Z) ChunkOf(int x, int z, int edge) => (FloorDiv(x, edge), FloorDiv(z, edge));

    private static int FloorDiv(int value, int by) => (int)Math.Floor((double)value / by);

    private static int Mod(int value, int by) => ((value % by) + by) % by;

    /// <summary>
    /// Reports whether the blocks are there to be read, and not only the record
    /// of where their surface is.
    ///
    /// A map chunk is the flat two-dimensional record a column keeps, holding its
    /// heightmaps and its climate, and the server keeps that long after it lets
    /// go of the blocks underneath. So
    /// <see cref="IWorldManagerAPI.GetMapChunk"/> answering does not mean the
    /// blocks are loaded, and the block accessor answers zero for every position
    /// in a chunk whose blocks are gone.
    ///
    /// Nothing further down reads that as a failure. The scan finds nothing that
    /// shows all the way to the bottom of the world, records air at the height the
    /// map chunk claims, and the renderer paints a flat brown chunk-aligned square
    /// in the middle of finished terrain. The square is stored, so it stays until
    /// something marks that chunk dirty again.
    ///
    /// The check is for the vertical chunk holding the highest ground in this
    /// column, because that is where the scan starts and, on ordinary terrain,
    /// where it stops.
    ///
    /// A rain height above the world is not a height. The game leaves
    /// `ushort.MaxValue` in that map wherever rain never stopped. Taking the
    /// highest number found let one such position speak for the whole chunk: the
    /// check asked for the vertical chunk two thousand layers up, was told there
    /// is none, and recorded a column whose blocks were all in memory as one whose
    /// blocks had gone. Nothing about a column changes what its rain map says, so
    /// that column stayed unreadable for the life of the world, and no export, no
    /// walk back to it and no chunk load could fill the hole.
    /// <see cref="Ceiling"/> excludes those heights.
    /// </summary>
    private static bool Readable(
        ICoreServerAPI api, int chunkX, int chunkZ, int edge, IMapChunk mapChunk)
    {
        return api.WorldManager.GetChunk(chunkX, Ceiling(api, mapChunk) / edge, chunkZ) is not null;
    }

    /// <summary>
    /// Returns the highest real ground in one chunk, in blocks.
    ///
    /// The rain map holds a height where rain stopped and `ushort.MaxValue` where
    /// it never did. Only the first kind names somewhere the world has a block, so
    /// anything at or above the top of the world is excluded.
    ///
    /// The result is zero where a chunk holds nothing else, which means a column
    /// open to the sky from top to bottom. The bottom of the world is a fine
    /// place to start looking for ground there, and that chunk certainly exists.
    /// </summary>
    private static int Ceiling(ICoreServerAPI api, IMapChunk mapChunk)
    {
        var world = api.WorldManager.MapSizeY;
        var top = 0;
        foreach (var height in mapChunk.RainHeightMap)
        {
            if (height > top && height < world)
            {
                top = height;
            }
        }
        return top;
    }

    /// <summary>
    /// Holds one chunk's surface while it is read and packed.
    ///
    /// The four parallel arrays are one thing: what is on top of each column of
    /// one chunk, in the order the format stores it. A caller reuses one instance
    /// across chunks, because an export walks hundreds of them and the buffer is
    /// the same size every time.
    /// </summary>
    public sealed class Surface
    {
        private readonly ushort[] _blocks;
        private readonly short[] _heights;
        private readonly byte[] _temperature;
        private readonly byte[] _rainfall;

        public Surface(int area)
        {
            _blocks = new ushort[area];
            _heights = new short[area];
            _temperature = new byte[area];
            _rainfall = new byte[area];
        }

        /// <summary>Records what is on top of one column.</summary>
        public void Set(int at, int block, int y, byte temperature, byte rainfall)
        {
            _blocks[at] = (ushort)block;
            _heights[at] = (short)y;
            _temperature[at] = temperature;
            _rainfall[at] = rainfall;
        }

        /// <summary>Packs the surface into the record the format stores.</summary>
        public byte[] Record()
        {
            var record = new byte[_blocks.Length * Regions.EntryBytes];
            for (var i = 0; i < _blocks.Length; i++)
            {
                var at = i * Regions.EntryBytes;
                record[at] = (byte)(_blocks[i] & 0xff);
                record[at + 1] = (byte)(_blocks[i] >> 8);
                record[at + 2] = (byte)(_heights[i] & 0xff);
                record[at + 3] = (byte)((_heights[i] >> 8) & 0xff);
                record[at + 4] = _temperature[i];
                record[at + 5] = _rainfall[i];
            }
            return record;
        }
    }

    /// <summary>
    /// Fills one chunk's columns. The rain height map already knows where the
    /// surface is, so this costs one block lookup and one climate lookup per
    /// column rather than a search down from the sky.
    /// </summary>
    private static void Read(
        ICoreServerAPI api,
        int chunkX,
        int chunkZ,
        int edge,
        IMapChunk mapChunk,
        Surface surface,
        System.Func<int, bool> shows,
        Microblocks chiselled)
    {
        var position = new BlockPos(0);
        for (var dz = 0; dz < edge; dz++)
        {
            for (var dx = 0; dx < edge; dx++)
            {
                var i = dz * edge + dx;
                ReadAt(api, mapChunk, chunkX * edge + dx, chunkZ * edge + dz, i, i, surface, position, shows, chiselled);
            }
        }
    }

    /// <summary>
    /// Reads what is on top of one column into one slot of a surface.
    /// </summary>
    /// <param name="index">The column's place in the chunk, which is where its
    /// rain height is.</param>
    /// <param name="slot">Where the answer goes. This matches
    /// <paramref name="index"/> for a whole chunk and is zero for a surface of
    /// one column.</param>
    private static void ReadAt(
        ICoreServerAPI api,
        IMapChunk mapChunk,
        int worldX,
        int worldZ,
        int index,
        int slot,
        Surface surface,
        BlockPos position,
        System.Func<int, bool> shows,
        Microblocks chiselled)
    {
        var accessor = api.World.BlockAccessor;
        var ceiling = api.WorldManager.MapSizeY - 1;

        // Clamped inside the world for the reason `Ceiling` clamps. The search
        // downward starts here, and a position whose rain never stopped reads
        // 65535. Starting there walks sixty thousand empty positions before
        // reaching the sky, and a column it finds nothing in stores that number
        // squeezed into a signed short, which is a surface at -1.
        var top = Math.Min((int)mapChunk.RainHeightMap[index], ceiling);
        var y = top;
        var id = 0;

        // The rain height map marks where rain stops, which is commonly the air
        // just above the ground, so step down until something is there.
        //
        // The search stops at a block that shows, not at any block that is not
        // air. A large structure stands one real block beside a run of invisible
        // placeholders, and a barrel or a door has its own. Stopping at the first
        // non-air block recorded one of those, and the map drew a speck of
        // nothing in the middle of grass. The palette decides what shows. See
        // `PaletteExchange.Shows`.
        //
        // The search runs all the way down rather than a few blocks. A dug shaft
        // is a column of air below where the sky still says the ground is, and
        // giving up after eight blocks recorded air, which the map paints as
        // unexplored ground. Every pit deeper than eight blocks became a hole no
        // export would fill.
        //
        // Only the columns that need the depth pay for it. Ordinary ground answers
        // on the first or second read.
        for (; y >= 0; y--)
        {
            position.Set(worldX, y, worldZ);
            var here = accessor.GetBlockId(position);
            if (here == 0)
            {
                continue;
            }

            // A chiselled block is a shell with its material in the block entity
            // beside it. See `Microblocks`. Every other block answers with
            // itself.
            //
            // The material is resolved before the palette is asked. The shell's
            // only texture is the game's missing-texture checker, so the palette
            // says it draws nothing. Asking the palette first would walk past
            // every ruin wall in the world and record the ground under it.
            var drawn = chiselled.MaterialAt(accessor, position, here, shows);
            if (shows(drawn))
            {
                id = drawn;
                break;
            }
        }

        if (id == 0)
        {
            // Nothing shows anywhere beneath the sky here. Record the height the
            // sky gave rather than a position below the world, so the height
            // stays a number the map can draw with.
            y = top;
        }

        position.Set(worldX, y, worldZ);

        // A column whose climate cannot be read takes the middle of both scales.
        // The buffer is reused across chunks, so leaving a slot unwritten would
        // keep the last chunk's value.
        var climate = accessor.GetClimateAt(position, EnumGetClimateMode.WorldGenValues);
        surface.Set(
            slot,
            id,
            y,
            climate is null ? (byte)128 : (byte)Climate.DescaleTemperature(climate.Temperature),
            climate is null ? (byte)128 : (byte)Math.Clamp((int)(climate.Rainfall * 255f), 0, 255));
    }
}
