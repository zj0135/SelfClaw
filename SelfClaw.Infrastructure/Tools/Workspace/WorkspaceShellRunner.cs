using System.Diagnostics;
using System.Text;
using SelfClaw.Core.Models;

namespace SelfClaw.Infrastructure.Tools.Workspace;

internal sealed class WorkspaceShellRunner
{
    private const int MaxShellOutputCharacters = 24_000;
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

        var standardOutputTask = process.StandardOutput.ReadToEndAsync();
        var standardErrorTask = process.StandardError.ReadToEndAsync();

        using var registration = cancellationToken.Register(() => WorkspaceProcess.TryKill(process));
        try
        {
            await process.WaitForExitAsync(cancellationToken).WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException exception)
        {
            throw new TimeoutException($"The PowerShell command timed out after {boundedTimeoutSeconds} seconds.", exception);
        }
        finally
        {
            WorkspaceProcess.TryKill(process);
        }

        var standardOutput = await standardOutputTask.ConfigureAwait(false);
        var standardError = await standardErrorTask.ConfigureAwait(false);
        return CreateResult(command, process.ExitCode, standardOutput, standardError);
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

    private static ShellCommandResult CreateResult(string command, int exitCode, string standardOutput, string standardError)
    {
        var outputTruncated = false;
        standardOutput = TruncateShellOutput(standardOutput, ref outputTruncated);
        standardError = TruncateShellOutput(standardError, ref outputTruncated);
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

    private static string TruncateShellOutput(string value, ref bool truncated)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= MaxShellOutputCharacters)
        {
            return value;
        }

        truncated = true;
        return value[..MaxShellOutputCharacters];
    }
}
