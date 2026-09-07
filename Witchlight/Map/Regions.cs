using System.Numerics;

namespace Witchlight;

/// <summary>
/// Holds the geometry the map is filed by.
///
/// A region is sixteen chunks on a side. At a chunk edge of 32 that is 512
/// blocks, which is both one map service tile at its finest level and the square
/// the game calls a map region. A tile and a game region are the same square, so
/// no coordinate needs converting between them.
///
/// Both halves of Witchlight share this arithmetic. Terrain travels over the API
/// channel and the service stores it, so nothing here touches disk.
/// </summary>
public static class Regions
{
    /// <summary>Chunks along a region's edge.</summary>
    public const int ChunksPerEdge = 16;

    /// <summary>
    /// The shift that divides a chunk coordinate by <see cref="ChunksPerEdge"/>.
    /// An arithmetic shift floors, which negative coordinates need. Deriving the
    /// shift keeps it correct when the edge size changes.
    /// </summary>
    private static readonly int Shift = BitOperations.Log2(ChunksPerEdge);

    /// <summary>
    /// The bytes a record holds for each column of a chunk: block id 2, surface y
    /// 2, temperature 1, rainfall 1, in the order the service reads them.
    /// </summary>
    public const int EntryBytes = 6;

    /// <summary>Returns the region a chunk belongs to. Negative coordinates floor.</summary>
    public static (int, int) Of(int chunkX, int chunkZ)
    {
        return (chunkX >> Shift, chunkZ >> Shift);
    }
}
