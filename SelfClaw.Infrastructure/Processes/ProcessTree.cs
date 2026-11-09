using System.ComponentModel;
using System.Diagnostics;

namespace SelfClaw.Infrastructure.Processes;

internal static class ProcessTree
{
    /// <summary>
    /// Ends a process and its descendants, and never throws. Every caller is a cancellation callback or a
    /// finally block, where an escaping exception does real damage: <see cref="CancellationTokenSource.Cancel()"/>
    /// rethrows it in place of the caller's <see cref="OperationCanceledException"/> (a cancelled turn would be
    /// recorded as a failed one and pause the queue), and a finally block that throws skips the rest of the
    /// cleanup after it.
    /// </summary>
    internal static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception) when (IsTerminationFailure(exception))
        {
        }
    }

    /// <summary>
    /// The shapes a failed termination surfaces as on Windows. <see cref="AggregateException"/> is the one
    /// <see cref="Process.Kill(bool)"/> uses for "not all processes in the descendant tree could be
    /// terminated" - a partial result, not a broken call. Retrying cannot help the members that refused, and
    /// the members that did die are already gone, so it is swallowed with the single-process failures. This
    /// stays a whitelist rather than <c>catch (Exception)</c> so a genuine bug in here is not hidden.
    /// </summary>
    internal static bool IsTerminationFailure(Exception exception)
        => exception is InvalidOperationException or Win32Exception or NotSupportedException or AggregateException;
}
