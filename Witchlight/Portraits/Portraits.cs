using System;
using System.IO;
using System.Text;

namespace Witchlight;

/// <summary>
/// Stores the pictures players send of themselves, and defines what one may be
/// and how often one may arrive.
///
/// Keeps a file per player beside the marker icons, served the same way. The
/// game's player uids are base64 and carry `+` and `/`, which read as a path, so
/// the file is named with the uid in hex. That name is not meant to be read, only
/// to be unambiguous, and both the file and the name handed to the viewer come
/// from here.
///
/// <see cref="QuietFor"/> and <see cref="LeastApart"/> are a pair and live in one
/// file so that tuning either keeps them in step. One is how fast a client sends
/// and the other is how fast the server will take.
/// </summary>
public static class Portraits
{
    /// <summary>
    /// The largest portrait the server will accept, in bytes.
    ///
    /// A 128 pixel PNG is a few kilobytes. This is a limit on what an untrusted
    /// client can make the server write, not a tuning figure.
    /// </summary>
    public const int Limit = 512 * 1024;

    /// <summary>
    /// How long a character must go unchanged before its client sends a picture.
    ///
    /// Long enough that a run of changes costs one wait, short enough that
    /// somebody who changes their hat and walks off is drawn wearing it.
    /// </summary>
    public const int QuietMs = 30000;

    /// <summary>
    /// The least time between two pictures a client may send unasked.
    ///
    /// Derived from <see cref="QuietFor"/>, which is the fastest an honest client
    /// sends on its own, since a change restarts the wait and two pictures cost two
    /// settles. Sits just under it so a slow packet never turns an honest picture
    /// into a refusal.
    ///
    /// Bounds only what a client sends of its own accord. A picture the server
    /// asked for does not count against it.
    /// </summary>
    public const int FloorMs = QuietMs - 5000;

    public static string DirectoryIn(string exports) => Path.Combine(exports, "portraits");

    /// <summary>Returns the name of a player's picture, without the extension.</summary>
    public static string NameFor(string uid)
    {
        var hex = new StringBuilder(uid.Length * 2);
        foreach (var b in Encoding.UTF8.GetBytes(uid))
        {
            hex.Append(b.ToString("x2"));
        }
        return hex.ToString();
    }

    /// <summary>
    /// Returns the name and modification time of a player's stored picture, or
    /// null when they have none. Both come from one look at the file.
    ///
    /// The name is derived from the player and never changes, so a redrawn player
    /// keeps it and the name alone cannot tell a new picture from the one it
    /// replaced. The time is what distinguishes two pictures under one name, which
    /// is what anything caching the last one needs.
    /// </summary>
    public static (string Name, long At)? StoredFor(string exports, string uid)
    {
        if (string.IsNullOrEmpty(uid))
        {
            return null;
        }

        var name = NameFor(uid);
        var file = new FileInfo(Path.Combine(DirectoryIn(exports), name + ".png"));
        return file.Exists
            ? (name, new DateTimeOffset(file.LastWriteTimeUtc).ToUnixTimeSeconds())
            : null;
    }

    /// <summary>
    /// Writes one picture and returns what happened.
    ///
    /// Checks the bytes are a PNG before writing any of them. They came off the
    /// network, and the server later serves the file under the name it wrote.
    /// </summary>
    public static bool Save(string exports, string uid, byte[]? png, out string said)
    {
        if (string.IsNullOrEmpty(uid))
        {
            said = "there is nobody to file it under";
            return false;
        }

        if (png is null || png.Length == 0)
        {
            said = "the picture was empty";
            return false;
        }

        if (png.Length > Limit)
        {
            said = $"the picture is {png.Length / 1024} KiB, over the {Limit / 1024} KiB limit";
            return false;
        }

        if (!IsPng(png))
        {
            said = "that is not a PNG";
            return false;
        }

        try
        {
            // A character taken apart and put back exactly as it was redraws to
            // the picture already on disk. Leave the stored file alone rather than
            // rewriting identical bytes.
            var path = Path.Combine(DirectoryIn(exports), NameFor(uid) + ".png");
            said = Disk.WriteBytes(path, png)
                ? $"{png.Length} bytes"
                : $"{png.Length} bytes, the same as the one stored";
            return true;
        }
        catch (Exception error)
        {
            said = error.Message;
            return false;
        }
    }

    /// <summary>Returns how many portraits are stored, for the status line.</summary>
    public static int Count(string exports)
    {
        try
        {
            return Directory.GetFiles(DirectoryIn(exports), "*.png").Length;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    /// <summary>The eight bytes every PNG starts with.</summary>
    private static bool IsPng(byte[] bytes) =>
        bytes.Length > 8
        && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47
        && bytes[4] == 0x0D && bytes[5] == 0x0A && bytes[6] == 0x1A && bytes[7] == 0x0A;
}
