using System;
using System.Text;

namespace Witchlight;

/// <summary>
/// Matches a preset's pattern against a block code.
///
/// The grammar is one rule: <c>*</c> stands for any run of characters and every
/// other character stands for itself. There are no escapes, so a reader can
/// check a pattern by eye. <c>game:ore-*-nativecopper-*</c> reaches copper in
/// every rock, and <c>game:rock</c> reaches one block.
///
/// The viewer holds a second copy of this rule in <c>presets.js</c>. The page
/// matches against the block under a pointer and this matches against the block
/// under a player, and neither side can wait on the other to answer a keypress.
/// Change one and change the other. The tests on each side come from the same
/// table of cases.
/// </summary>
public static class BlockPattern
{
    /// <summary>Reports whether this pattern matches this block code.</summary>
    public static bool Fits(string? pattern, string? code)
    {
        if (string.IsNullOrEmpty(pattern) || string.IsNullOrEmpty(code))
        {
            return false;
        }

        var parts = pattern.ToLowerInvariant().Split('*');
        var named = code.ToLowerInvariant();
        var reached = 0;

        for (var i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            if (part.Length == 0)
            {
                continue;
            }

            var found = i == 0
                ? (named.StartsWith(part, StringComparison.Ordinal) ? 0 : -1)
                : named.IndexOf(part, reached, StringComparison.Ordinal);
            if (found < 0)
            {
                return false;
            }
            reached = found + part.Length;
        }

        // A pattern not ending in `*` must reach the end of the code, or
        // `rock-*` and `rock` would both match every rock.
        var last = parts[^1];
        return last.Length == 0 || named.EndsWith(last, StringComparison.Ordinal);
    }

    /// <summary>
    /// Returns the default pattern for a block code, with each run of digits
    /// replaced by a wildcard.
    ///
    /// Block codes carry their variant as a number, as in
    /// <c>game:leaves-grown7-oak</c> or <c>game:tallgrass-3</c>. A preset kept
    /// against one of those matches exactly one stage out of eight, so covering
    /// grass would take eight presets. Widening the number to a wildcard makes
    /// <c>game:leaves-grown*-oak</c> cover them all.
    ///
    /// This is only a default. The star is a character in a text field, so a user
    /// can move it, double it, or remove it to name one block exactly.
    /// <see cref="Fits"/> reads it wherever it ends up. A code with no number in
    /// it is its own pattern.
    ///
    /// The viewer's form offers the same default, so a preset made from a key
    /// press and one made from a right click start out the same. See `widened` in
    /// the viewer's `presets.js`.
    /// </summary>
    public static string Widened(string? code)
    {
        if (string.IsNullOrEmpty(code))
        {
            return "";
        }

        var widened = new StringBuilder(code.Length);
        var inNumber = false;
        foreach (var letter in code)
        {
            if (char.IsAsciiDigit(letter))
            {
                if (!inNumber)
                {
                    widened.Append('*');
                    inNumber = true;
                }
                continue;
            }

            inNumber = false;
            widened.Append(letter);
        }

        return widened.ToString();
    }
}
