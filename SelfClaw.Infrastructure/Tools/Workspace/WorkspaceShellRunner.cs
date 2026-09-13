using System.Diagnostics;
using System.Text;
using SelfClaw.Core.Models;

namespace SelfClaw.Infrastructure.Tools.Workspace;

internal sealed class WorkspaceShellRunner
{
    internal const int MaxShellOutputCharacters = 24_000;
    private const int MinShellTimeoutSeconds = 1;
    private const int MaxShellTimeoutSeconds = 600;

    public async Task<ShellCommandResult> RunShellCommandAsync(
        string workspaceRootPath,
        string command,
        int timeoutSeconds,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(command))
        {
            throw new ArgumentException("A shell command is required.", nameof(command));
        }

        var root = WorkspaceFileAccess.NormalizeRoot(workspaceRootPath);
        var boundedTimeoutSeconds = Math.Clamp(timeoutSeconds, MinShellTimeoutSeconds, MaxShellTimeoutSeconds);
        var timeout = TimeSpan.FromSeconds(boundedTimeoutSeconds);

        using var process = CreateProcess(root, command);

        if (!process.Start())
        {
            throw new InvalidOperationException("Failed to start the PowerShell process.");
        }

        using var drainCancellation = new CancellationTokenSource();
        var standardOutputTask = DrainAsync(process.StandardOutput, drainCancellation.Token);
        var standardErrorTask = DrainAsync(process.StandardError, drainCancellation.Token);
        var drains = Task.WhenAll(standardOutputTask, standardErrorTask);

        using var registration = cancellationToken.Register(() => WorkspaceProcess.TryKill(process));
        try
        {
            await Task.WhenAll(process.WaitForExitAsync(cancellationToken), drains)
                .WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            var standardOutput = await standardOutputTask.ConfigureAwait(false);
            var standardError = await standardErrorTask.ConfigureAwait(false);
            return CreateResult(command, process.ExitCode, standardOutput.Text, standardError.Text,
                standardOutput.Truncated || standardError.Truncated);
        }
        catch (TimeoutException exception)
        {
            throw new TimeoutException($"The PowerShell command timed out after {boundedTimeoutSeconds} seconds.", exception);
        }
        finally
        {
            WorkspaceProcess.TryKill(process);
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

    private static async Task<(string Text, bool Truncated)> DrainAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var output = new StringBuilder(MaxShellOutputCharacters);
        var buffer = new char[4096];
        var truncated = false;
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
        {
            var retained = Math.Min(read, MaxShellOutputCharacters - output.Length);
            output.Append(buffer, 0, retained);
            truncated |= retained < read;
        }

        if (truncated && output.Length > 0 && char.IsHighSurrogate(output[^1])) output.Length--;
        return (output.ToString(), truncated);
    }

    private static Process CreateProcess(string root, string command)
    {
        var script = string.Join(
            Environment.NewLine,
            "[Console]::InputEncoding = [System.Text.UTF8Encoding]::new($false)",
            "[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)",
            "$OutputEncoding = [System.Text.UTF8Encoding]::new($false)",
            command);
        var encodedCommand = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));

        return new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encodedCommand}",
                WorkingDirectory = root,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            }
        };
    }

    private static ShellCommandResult CreateResult(string command, int exitCode, string standardOutput, string standardError, bool outputTruncated)
    {
        return new ShellCommandResult(
            command,
            true,
            exitCode,
            standardOutput,
            standardError,
            outputTruncated,
            exitCode == 0
                ? "PowerShell command completed."
                : $"PowerShell exited with code {exitCode}.");
    }

}
