using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace Witchlight;

/// <summary>
/// Resolves the material a chiselled block is made of.
///
/// A microblock is the game's chiselled block, and every ruin's stonework is
/// built out of them. It carries no colour of its own. The shape and the
/// material live in the block entity beside it, and the world reports the same
/// `chiseledblock` at that position whether it was cut from granite or from
/// cobblestone. The palette can only answer for the block it is handed, and that
/// answer is the near-white of an untextured shell, so every ruin drew as a white
/// patch on correctly coloured ground.
///
/// This class asks the block entity instead. The game's
/// <c>GetMajorityMaterialId</c> already answers the question a map pixel asks,
/// so reading the voxels here would be more work for a worse answer.
/// </summary>
public sealed class Microblocks
{
    /// <summary>
    /// Holds the block ids whose material needs looking up. A column that is not
    /// chiselled costs one set lookup and no block entity read.
    /// </summary>
    private readonly HashSet<int> _shells;

    private Microblocks(HashSet<int> shells) => _shells = shells;

    /// <summary>
    /// Collects every kind of chiselled block this world has registered,
    /// including the snow-covered variants. The shell's own colour is the
    /// missing-texture checker rather than snow, so a snow-covered ruin left out
    /// of this set drew as white on white.
    /// </summary>
    public static Microblocks In(IWorldAccessor world)
    {
        var shells = new HashSet<int>();
        foreach (var block in world.Blocks)
        {
            if (block is BlockMicroBlock)
            {
                shells.Add(block.Id);
            }
        }

        return new Microblocks(shells);
    }

    /// <summary>Returns how many kinds of chiselled block this set covers.</summary>
    public int Kinds => _shells.Count;

    /// <summary>
    /// Returns the material to record for the block at a position: the material a
    /// chiselled block is mostly made of, or the block id itself where it is not
    /// chiselled.
    ///
    /// <paramref name="shows"/> is passed to the game rather than applied to the
    /// answer, so the majority runs over materials the map can paint. A block
    /// chiselled partly out of something invisible then answers with the part
    /// that draws.
    ///
    /// A block whose entity has gone, or one made of nothing the palette knows,
    /// falls back to the block id.
    /// </summary>
    public int MaterialAt(IBlockAccessor accessor, BlockPos at, int id, System.Func<int, bool> shows)
    {
        if (!_shells.Contains(id))
        {
            return id;
        }

        if (accessor.GetBlockEntity(at) is not BlockEntityMicroBlock shell)
        {
            return id;
        }

        var material = shell.GetMajorityMaterialId(material => shows(material));
        return material > 0 ? material : id;
    }
}
