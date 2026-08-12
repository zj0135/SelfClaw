using System.ComponentModel;
using System.Diagnostics;

namespace SelfClaw.Infrastructure.Git;

internal sealed class GitCommandRunner
{
    public async Task<GitCommandResult> RunAsync(
        string workingDirectory,
        IEnumerable<string> arguments,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ArgumentNullException.ThrowIfNull(arguments);

        var startInfo = new ProcessStartInfo
        {
            FileName = "git.exe",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("Git could not be started.");
            }
        }
        catch (Win32Exception exception)
        {
            throw new InvalidOperationException("Git is not installed or git.exe is not on PATH.", exception);
        }

        using var cancellationRegistration = cancellationToken.Register(static state =>
        {
            var runningProcess = (Process)state!;
            try
            {
                if (!runningProcess.HasExited)
                {
                    runningProcess.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
            }
            catch (NotSupportedException)
            {
            }
        }, process);

        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return new GitCommandResult(
            process.ExitCode,
            await outputTask.ConfigureAwait(false),
            await errorTask.ConfigureAwait(false));
    }
}
