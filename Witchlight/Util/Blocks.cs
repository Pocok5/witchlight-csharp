using System;

namespace Witchlight;

/// <summary>
/// Converts a world position to the block coordinate that contains it.
///
/// The conversion floors rather than casts. A cast rounds toward zero, which
/// names the block one to the east and south of the one a negative coordinate is
/// really in.
/// </summary>
public static class Blocks
{
    public static int At(double position) => (int)Math.Floor(position);
}
