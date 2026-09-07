namespace Witchlight;

/// <summary>
/// Computes the CRC-32 checksum that deflate uses.
///
/// The polynomial must match the one the map service's compression library
/// carries. The mod compares the two checksums to tell a chunk loading again
/// from one that changed, without holding a copy of the ground.
/// </summary>
public static class Crc32
{
    private static readonly uint[] Table = Build();

    private static uint[] Build()
    {
        var table = new uint[256];
        for (uint at = 0; at < 256; at++)
        {
            var value = at;
            for (var bit = 0; bit < 8; bit++)
            {
                value = (value & 1) != 0 ? 0xEDB88320u ^ (value >> 1) : value >> 1;
            }
            table[at] = value;
        }
        return table;
    }

    public static uint Of(byte[] bytes)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var one in bytes)
        {
            crc = Table[(crc ^ one) & 0xFF] ^ (crc >> 8);
        }
        return crc ^ 0xFFFFFFFFu;
    }
}
