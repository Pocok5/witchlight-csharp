using System.Text;

namespace Witchlight;

/// <summary>
/// Computes FNV-1a hashes of strings.
///
/// The hash is not cryptographic and not a checksum. It tells two values apart
/// and stays short enough to read.
///
/// Hashing always runs over UTF-8 bytes. Hashing UTF-16 chars gives a different
/// answer for text outside ASCII, so the encoding is fixed here.
/// </summary>
public static class Fnv1a
{
    private const ulong Offset = 0xcbf29ce484222325UL;
    private const ulong Prime = 0x100000001b3UL;

    /// <summary>Hashes one string, as sixteen hex digits.</summary>
    public static string Of(string text)
    {
        return Text(Offset, text).ToString("x16");
    }

    /// <summary>
    /// Hashes several strings together, as sixteen hex digits.
    ///
    /// The parts fold in order, so the same parts in a different order give a
    /// different hash.
    /// </summary>
    public static string Of(params string[] parts)
    {
        var value = Offset;
        foreach (var part in parts)
        {
            value = Text(value, part);
        }
        return value.ToString("x16");
    }

    /// <summary>Folds one string into a running value.</summary>
    private static ulong Text(ulong value, string text)
    {
        foreach (var b in Encoding.UTF8.GetBytes(text ?? ""))
        {
            value ^= b;
            value *= Prime;
        }
        return value;
    }
}
