using System;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace Witchlight;

/// <summary>
/// Extracts the map service binary from the mod archive.
///
/// The service is a separate program. The binary travels inside the mod so it is
/// not a second thing to install, and this class writes it out where it can be
/// run. <see cref="ServiceProcess"/> runs it.
/// </summary>
public static class BundledService
{
    /// <summary>The path of the Linux binary inside the mod archive.</summary>
    private const string LinuxAt = "service/linux-x64/witchlight";

    /// <summary>The path of the Windows binary inside the mod archive.</summary>
    private const string WindowsAt = "service/win-x64/witchlight.exe";

    /// <summary>
    /// The path of the binary this machine runs, or null when the mod carries
    /// none for it.
    ///
    /// An archive can hold one platform or both. This reports where to look for
    /// the one that matches, and the caller treats a missing entry as a mod
    /// packaged without a service.
    /// </summary>
    private static string? BundledAt =>
        RuntimeInformation.ProcessArchitecture != Architecture.X64 ? null
        : RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? LinuxAt
        : RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? WindowsAt
        : null;

    /// <summary>The name of the unpacked binary.</summary>
    private static string Name => OperatingSystem.IsWindows() ? "witchlight.exe" : "witchlight";

    /// <summary>
    /// Writes the bundled service out where it can be run, once per version, and
    /// returns its path.
    ///
    /// Returns null and logs when this machine has no service to run, either
    /// because nothing was bundled for this platform or because the archive was
    /// packaged without one.
    ///
    /// Rewrites the unpacked copy whenever the archive is newer, which is what
    /// upgrading the mod does. That is how the running service stays the one that
    /// came with this build.
    /// </summary>
    public static string? Unpack(ICoreServerAPI api, Mod mod)
    {
        if (BundledAt is not { } bundledAt)
        {
            api.Logger.Notification(
                "[witchlight] no bundled map service for {0} {1} — the map will export as usual, "
                + "and `witchlight serve` run yourself will serve it",
                RuntimeInformation.OSDescription,
                RuntimeInformation.ProcessArchitecture);
            return null;
        }

        var into = Path.Combine(GamePaths.Cache, "witchlight");
        var executable = Path.Combine(into, Name);

        try
        {
            // A mod loads from a folder while it is developed and from an
            // archive once it is installed.
            var source = mod.SourcePath;
            var folder = Directory.Exists(source);
            var origin = folder ? Path.Combine(source, bundledAt) : source;

            if (!File.Exists(origin))
            {
                return Missing(api, source, bundledAt);
            }

            var packed = File.GetLastWriteTimeUtc(origin);
            if (File.Exists(executable) && File.GetLastWriteTimeUtc(executable) == packed)
            {
                return executable;
            }

            Directory.CreateDirectory(into);

            if (folder)
            {
                File.Copy(origin, executable, overwrite: true);
            }
            else
            {
                using var archive = ZipFile.OpenRead(origin);
                if (archive.GetEntry(bundledAt) is not { } entry)
                {
                    return Missing(api, source, bundledAt);
                }
                entry.ExtractToFile(executable, overwrite: true);
            }

            // Stamp the copy with the archive's own time, so the check above can
            // tell whether this is the service that came with this build.
            // Extraction otherwise stamps the copy with the entry's time, which
            // is when the service was compiled and does not compare against the
            // archive's time.
            File.SetLastWriteTimeUtc(executable, packed);
            MakeRunnable(executable);

            api.Logger.Notification("[witchlight] unpacked the map service to {0}", executable);
            return executable;
        }
        catch (Exception error)
        {
            api.Logger.Error("[witchlight] could not unpack the map service: {0}", error);
            return null;
        }
    }

    /// <summary>
    /// Sets the execute bits on the unpacked binary.
    ///
    /// Windows has no execute bit and needs nothing done, so this returns there.
    /// The test is made here rather than at the call site because the compiler
    /// cannot see it from three call frames away, and neither can the next
    /// reader of this line.
    /// </summary>
    private static void MakeRunnable(string executable)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        File.SetUnixFileMode(
            executable,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
    }

    private static string? Missing(ICoreServerAPI api, string source, string bundledAt)
    {
        api.Logger.Warning(
            "[witchlight] {0} carries no map service at {1} — it was packaged without one, "
            + "or without the one this machine needs. The map will export as usual, and "
            + "`witchlight serve` run yourself will serve it",
            source, bundledAt);
        return null;
    }
}
