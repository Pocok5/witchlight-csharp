using System;
using System.IO;

namespace Witchlight;

/// <summary>
/// Writes files atomically, and only when the content differs from what is
/// already on disk.
///
/// Every write costs drive life, and the exports here are mostly the same bytes
/// run after run. Comparing first skips those writes.
///
/// The map service reads these files while the server runs. Each write goes to a
/// temporary beside the file and is renamed into place, so a reader sees either
/// the old bytes or the new ones and never half of either.
/// </summary>
public static class Disk
{
    /// <summary>
    /// Writes text where it differs from what is on disk. Returns true when it
    /// wrote.
    ///
    /// An unreadable file counts as differing and is written. Being wrong that
    /// way costs one write. Being wrong the other way costs an export that never
    /// lands.
    /// </summary>
    public static bool Write(string path, string body)
    {
        if (Same(path, () => File.ReadAllText(path) == body))
        {
            return false;
        }

        Replace(path, temporary => File.WriteAllText(temporary, body));
        return true;
    }

    /// <summary>Writes bytes where they differ from what is on disk. Returns true when it wrote.</summary>
    public static bool WriteBytes(string path, byte[] body)
    {
        // Comparing lengths first skips the read for most differing files.
        if (Same(path, () => new FileInfo(path).Length == body.Length
                             && File.ReadAllBytes(path).AsSpan().SequenceEqual(body)))
        {
            return false;
        }

        Replace(path, temporary => File.WriteAllBytes(temporary, body));
        return true;
    }

    /// <summary>
    /// Writes through a temporary beside the file and renames it into place.
    ///
    /// This is public for callers that produce their bytes as they write, such as
    /// a compressed stream, and so have nothing to compare first.
    /// </summary>
    public static void Replace(string path, Action<string> write)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporary = path + ".part";
        try
        {
            write(temporary);
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception)
        {
            // A part-written temporary blocks the next attempt.
            try
            {
                File.Delete(temporary);
            }
            catch (Exception)
            {
                // Nothing further to try. The original is still intact.
            }
            throw;
        }
    }

    /// <summary>
    /// Reports whether the stored file already holds exactly this content.
    ///
    /// An unreadable file reports false, so the caller overwrites it.
    /// </summary>
    private static bool Same(string path, Func<bool> matches)
    {
        try
        {
            return File.Exists(path) && matches();
        }
        catch (Exception)
        {
            return false;
        }
    }
}
