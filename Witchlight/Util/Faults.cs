using System;
using System.Collections.Generic;
using Vintagestory.API.Common;

namespace Witchlight;

/// <summary>
/// Runs a piece of work and swallows any exception it throws.
///
/// The server records a tick listener as having run only after its handler
/// returns. A handler that throws leaves the listener permanently due, so it
/// fires again on the next pass of the server loop. One unready entity produced
/// a hundred thousand identical errors in four seconds, which crossed the
/// server's own error threshold and shut it down.
///
/// Each kind of failure is logged once and then held quiet. A different
/// exception from the same work counts as a different failure and is logged.
/// </summary>
public sealed class Faults(ILogger log)
{
    /// <summary>Holds the failures already logged, so none is logged twice.</summary>
    private readonly HashSet<string> _reported = new(StringComparer.Ordinal);

    /// <summary>Runs the work and swallows any exception it throws.</summary>
    public void Doing(string what, Action work)
    {
        try
        {
            work();
        }
        catch (Exception error)
        {
            if (_reported.Add($"{what}/{error.GetType().Name}"))
            {
                log.Error(
                    "[witchlight] {0} failed, and this will not be reported again: {1}", what, error);
            }
        }
    }
}
