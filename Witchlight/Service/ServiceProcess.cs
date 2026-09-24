using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;

namespace Witchlight;

/// <summary>
/// Runs the map service as a child of the game server.
///
/// The service is a separate program. Running it from here means an operator does
/// not have to install, configure and start a second thing. One setting turns
/// this off, and the service can then be started by hand.
///
/// Shutdown kills the process outright. The service writes every file beside
/// itself and renames it into place, so a kill leaves no half-written file.
/// </summary>
public sealed class ServiceProcess : IDisposable
{
    private readonly ICoreServerAPI _api;
    private readonly string _executable;
    private readonly string _config;

    private Process? _process;
    private StreamWriter? _log;
    private readonly object _writing = new();

    /// <summary>
    /// True while a stop this class asked for is in progress.
    ///
    /// Volatile because the game thread writes it and <see cref="Ended"/> reads
    /// it on whichever threadpool thread the exit event arrives on. It picks
    /// which line goes in the log, so a stale read reports a deliberate stop as a
    /// crash.
    /// </summary>
    private volatile bool _stopping;

    private ServiceProcess(ICoreServerAPI api, string executable, string config)
    {
        _api = api;
        _executable = executable;
        _config = config;
    }

    /// <summary>The path of the service's own log file.</summary>
    public static string LogPath => Path.Combine(GamePaths.Logs, "witchlight-service.log");

    /// <summary>The path the previous run's log is kept at.</summary>
    public static string PreviousLogPath =>
        Path.Combine(GamePaths.Logs, "witchlight-service.previous.log");

    /// <summary>True while this class's service process is running.</summary>
    public bool Running => _process is { HasExited: false };

    /// <summary>
    /// Moves the last run's log aside, so starting the service again does not
    /// erase why the last one stopped.
    ///
    /// Keeps one run back and no more. Two files an operator can name beat a
    /// numbered set nobody prunes, and the run before last has never been the
    /// one asked for.
    ///
    /// A failure here is not worth refusing to start over. The log is how a
    /// fault is read, not part of serving the map, so this reports and carries
    /// on.
    /// </summary>
    private void KeepLastLog()
    {
        try
        {
            if (!File.Exists(LogPath) || new FileInfo(LogPath).Length == 0)
            {
                return;
            }

            File.Move(LogPath, PreviousLogPath, overwrite: true);
        }
        catch (Exception error)
        {
            _api.Logger.Warning(
                "[witchlight] could not keep the last service log: {0}", error.Message);
        }
    }

    /// <summary>
    /// Unpacks the bundled service and creates its configuration file, without
    /// starting anything. Returns null and logs when this machine has no service
    /// to run.
    /// </summary>
    public static ServiceProcess? Prepare(ICoreServerAPI api, Mod mod)
    {
        if (BundledService.Unpack(api, mod) is not { } executable)
        {
            return null;
        }

        var config = Settings.EnsureWritten(api, executable);
        return config is null ? null : new ServiceProcess(api, executable, config);
    }

    /// <summary>True when the settings ask the mod to start the service itself.</summary>
    public bool Wanted => Settings.Autostarts;

    /// <summary>
    /// Starts the service and returns one sentence saying what happened.
    ///
    /// Sends the service's output to <see cref="LogPath"/> rather than the
    /// server's log. The service has its own version, errors and pace, and either
    /// log is easier to read when the two are not interleaved.
    /// </summary>
    public string Start()
    {
        if (Running)
        {
            return $"the map service is already running (pid {_process!.Id})";
        }

        try
        {
            var started = new ProcessStartInfo(_executable)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Settings.DataPath,
            };
            started.ArgumentList.Add("--config");
            started.ArgumentList.Add(_config);
            // Name the export directory outright. Where each world keeps its own
            // map there are several to choose between, and only the mod knows
            // which world is running.
            started.ArgumentList.Add("--exports");
            started.ArgumentList.Add(Settings.Exports);
            started.ArgumentList.Add("serve");

            // Clear what a previous run published about where it was listening,
            // so the wait below reads this service's answer and not the last
            // one's.
            Forget();

            // Keep the last run's log before this one truncates it.
            //
            // A service that stopped on its own wrote why it stopped into this
            // file, and the first thing an operator does is start it again,
            // which is what would erase it. The copy is what they read after
            // that.
            Directory.CreateDirectory(GamePaths.Logs);
            KeepLastLog();

            // Truncate the log on start, and share it so a tail already watching
            // it keeps working across a restart.
            //
            // Open it under the lock that writes it. A start comes off the game
            // thread while every line written to it arrives on a threadpool
            // thread. Publishing the writer outside the lock would make it
            // reachable before `AutoFlush` is set. Dispose whatever a previous
            // run left open, since a service that stopped on its own never
            // closed one.
            lock (_writing)
            {
                _log?.Dispose();
                _log = new StreamWriter(
                    new FileStream(LogPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
                {
                    AutoFlush = true,
                };
            }

            var process = new Process { StartInfo = started, EnableRaisingEvents = true };
            process.OutputDataReceived += (_, line) => Say(line.Data);
            process.ErrorDataReceived += (_, line) => Say(line.Data);
            process.Exited += (who, _) => Ended(who as Process);

            _stopping = false;

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            _process = process;

            _api.Logger.Notification(
                "[witchlight] map service started (pid {0}), logging to {1}", process.Id, LogPath);
            SayWhereItIsListening();
            return $"the map service is running (pid {process.Id}), logging to {LogPath}";
        }
        catch (Exception error)
        {
            _api.Logger.Error("[witchlight] could not start the map service: {0}", error);
            return $"could not start the map service: {error.Message}";
        }
    }

    /// <summary>Stops the service and returns one sentence saying what happened.</summary>
    public string Stop()
    {
        var process = _process;
        if (process is null || process.HasExited)
        {
            _process = null;
            CloseLog();
            return "the map service is not running";
        }

        try
        {
            var pid = process.Id;
            _stopping = true;
            process.Kill(entireProcessTree: true);
            process.WaitForExit(5000);
            return $"the map service has been stopped (pid {pid})";
        }
        catch (Exception error)
        {
            return $"could not stop the map service: {error.Message}";
        }
        finally
        {
            _process = null;
            Forget();
            CloseLog();
        }
    }

    /// <summary>Returns one line describing the service, for `/witchlight status`.</summary>
    public string Describe() => Running
        ? $"service: running (pid {_process!.Id}), log at {LogPath}"
        : Wanted
            ? $"service: not running — see {LogPath}, and `/witchlight service start`"
            : "service: not started, autostart is off in " + _config;

    /// <summary>
    /// Waits for the service to publish its address, then logs it to the server's
    /// own log, where an operator is already reading.
    ///
    /// Waits rather than working the address out here. The service resolves which
    /// addresses a bind such as `0.0.0.0` answers on, and it has already done so.
    /// </summary>
    private void SayWhereItIsListening()
    {
        Task.Run(async () =>
        {
            var giveUpAt = DateTime.UtcNow + WaitForAddress;
            while (DateTime.UtcNow < giveUpAt)
            {
                if (!Running)
                {
                    // It stopped. Ended() has already said so, and twice is noise.
                    return;
                }

                if (Settings.Address() is { } at)
                {
                    _api.Logger.Notification("[witchlight] the map is being served at {0}", at);
                    return;
                }

                await Task.Delay(250);
            }

            _api.Logger.Warning(
                "[witchlight] the map service has not said where it is listening. See {0}", LogPath);
        });
    }

    /// <summary>
    /// How long to wait for the service to publish its address. Long enough for a
    /// cold start on a slow disk, short enough to report a service that will
    /// never answer while somebody is still watching.
    /// </summary>
    private static readonly TimeSpan WaitForAddress = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Deletes everything the service published about itself.
    ///
    /// Stops the mod handing a player the address of a map that is not there, and
    /// stops it posting live positions to whatever took the service's port next.
    /// The second matters more: an address that has gone answers nothing, while a
    /// port something else has taken answers.
    /// </summary>
    private void Forget()
    {
        foreach (var path in new[]
                 {
                     Settings.AddressPath,
                     MapService.ConnectionPath(Settings.Exports),
                 })
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception)
            {
                // A service that cannot be reached says so by not answering.
            }
        }
    }

    private void Say(string? line)
    {
        if (line is null)
        {
            return;
        }

        lock (_writing)
        {
            _log?.WriteLine(line);
        }
    }

    /// <summary>
    /// Logs that the service stopped, more loudly when nobody asked it to.
    ///
    /// Does not restart it. A service that will not start, because its port is
    /// taken or its settings cannot be read, fails the same way every time, and a
    /// restart loop turns one legible error into an unreadable log.
    /// </summary>
    private void Ended(Process? which)
    {
        // Read the exit code from the process the event carries. The _process
        // field may already be cleared by the time a deliberate stop reaches
        // here, which would report no exit code at all.
        var code = Exited(which);

        if (_stopping)
        {
            Say($"witchlight: stopped (exit {code})");
            return;
        }

        Say($"witchlight: stopped on its own (exit {code})");
        _api.Logger.Warning(
            "[witchlight] the map service stopped on its own (exit {0}: {1}). Its log is {2}, "
            + "and starting it again keeps that log at {3}. `/witchlight service start` runs it "
            + "again",
            code, Meaning(code), LogPath, PreviousLogPath);
    }

    /// <summary>
    /// Says what an exit code means, in the words of what happened rather than
    /// the number.
    ///
    /// The number on its own sends an operator to a search engine. These four
    /// cover every way this service has been seen to stop.
    /// </summary>
    private static string Meaning(string code) => code switch
    {
        "0" => "it ended without being asked to, which it should not do while serving",
        "1" => "it reported a fault it could not carry on from, named on the last lines of its log",
        "101" => "it panicked; the panic and the thread it happened on are in its log",
        "127" => "the executable could not be run at all, so nothing was logged",
        "134" => "it was aborted",
        "137" => "it was killed, which on most machines means the system ran out of memory",
        "139" => "it stopped on a memory fault",
        "143" => "something outside the game asked it to stop",
        _ => "an unexpected stop; its log holds whatever it managed to say",
    };

    /// <summary>Returns the process's exit code, or "unknown" when it has none.</summary>
    private static string Exited(Process? which)
    {
        try
        {
            return which is null ? "unknown" : which.ExitCode.ToString();
        }
        catch (Exception)
        {
            return "unknown";
        }
    }

    private void CloseLog()
    {
        lock (_writing)
        {
            _log?.Dispose();
            _log = null;
        }
    }

    public void Dispose()
    {
        var process = _process;
        _stopping = true;
        _process = null;

        try
        {
            if (process is { HasExited: false })
            {
                // Safe to kill. The service writes every file beside itself and
                // renames it into place, so nothing is caught half written.
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
            process?.Dispose();
        }
        catch (Exception)
        {
            // Shutting down. A service that has already gone is the outcome.
        }

        Forget();
        CloseLog();
    }
}
