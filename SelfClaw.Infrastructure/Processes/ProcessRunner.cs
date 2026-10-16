using System.Diagnostics;
using System.Text;

namespace SelfClaw.Infrastructure.Processes;

/// <summary>
/// Runs a child process with bounded output, full-tree cancellation and a hard timeout. Every
/// process-launching feature shares this one implementation: the drain loop has to keep reading both
/// pipes after the caller stops caring or the child blocks on a full pipe and never exits.
/// </summary>
internal static class ProcessRunner
{
    internal const int MaxOutputCharacters = 24_000;
    internal const int MinTimeoutSeconds = 1;
    internal const int MaxTimeoutSeconds = 600;

    internal static int ClampTimeoutSeconds(int timeoutSeconds)
        => Math.Clamp(timeoutSeconds, MinTimeoutSeconds, MaxTimeoutSeconds);

    /// <param name="timeoutSubject">
    /// Named in the timeout message, e.g. "PowerShell command", so the failure still reads like the
    /// feature the user invoked.
    /// </param>
    internal static async Task<ProcessRunResult> RunAsync(
        ProcessStartInfo startInfo,
        int timeoutSeconds,
        string timeoutSubject,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        cancellationToken.ThrowIfCancellationRequested();
        startInfo.UseShellExecute = false;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        startInfo.CreateNoWindow = true;
        startInfo.StandardOutputEncoding = Encoding.UTF8;
        startInfo.StandardErrorEncoding = Encoding.UTF8;
        var boundedTimeoutSeconds = ClampTimeoutSeconds(timeoutSeconds);
        var timeout = TimeSpan.FromSeconds(boundedTimeoutSeconds);

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to start '{startInfo.FileName}'.");
        }

        using var drainCancellation = new CancellationTokenSource();
        var standardOutputTask = DrainAsync(process.StandardOutput, drainCancellation.Token);
        var standardErrorTask = DrainAsync(process.StandardError, drainCancellation.Token);
        var drains = Task.WhenAll(standardOutputTask, standardErrorTask);

        using var registration = cancellationToken.Register(() => ProcessTree.TryKill(process));
        try
        {
            await Task.WhenAll(process.WaitForExitAsync(cancellationToken), drains)
                .WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            // The cancellation callback kills the process, which can let the wait above complete
            // normally before the token is observed; caller cancellation stays authoritative.
            cancellationToken.ThrowIfCancellationRequested();
            var standardOutput = await standardOutputTask.ConfigureAwait(false);
            var standardError = await standardErrorTask.ConfigureAwait(false);
            return new ProcessRunResult(
                process.ExitCode,
                standardOutput.Text,
                standardError.Text,
                standardOutput.Truncated || standardError.Truncated);
        }
        catch (TimeoutException exception)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException(
                $"The {timeoutSubject} timed out after {boundedTimeoutSeconds} seconds.", exception);
        }
        finally
        {
            ProcessTree.TryKill(process);
            // These readers use an internal shutdown token; caller cancellation still propagates
            // from the wait above, while inherited pipes get at most two seconds to close.
            drainCancellation.CancelAfter(TimeSpan.FromSeconds(2));
            try
            {
                await drains.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (drainCancellation.IsCancellationRequested)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
        }
    }

    private static async Task<(string Text, bool Truncated)> DrainAsync(
        StreamReader reader,
        CancellationToken cancellationToken)
    {
        var output = new StringBuilder(MaxOutputCharacters);
        var buffer = new char[4096];
        var truncated = false;
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
        {
            var retained = Math.Min(read, MaxOutputCharacters - output.Length);
            output.Append(buffer, 0, retained);
            truncated |= retained < read;
        }

        if (truncated && output.Length > 0 && char.IsHighSurrogate(output[^1]))
        {
            output.Length--;
        }

        return (output.ToString(), truncated);
    }
}
