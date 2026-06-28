using System.Diagnostics;

namespace SelfClaw.Infrastructure.Agents.Cli.Process;

/// <summary>
/// Default <see cref="ICliAgentProcessHost"/> backed by <see cref="System.Diagnostics.Process"/>.
/// Launches a CLI agent subprocess with stdin/stdout/stderr redirected (plan.md §5) and hands the
/// caller a <see cref="CliAgentProcessSession"/> that owns the turn lifecycle.
/// </summary>
public sealed class CliAgentProcessHost : ICliAgentProcessHost
{
    public ICliAgentProcessSession Start(
        CliProcessStartInfo startInfo,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(startInfo);

        var invocation = startInfo.Invocation
            ?? throw new ArgumentException("StartInfo.Invocation is required.", nameof(startInfo));

        var psi = new ProcessStartInfo
        {
            FileName = invocation.FileName,
            WorkingDirectory = string.IsNullOrEmpty(startInfo.WorkingDirectory)
                ? Path.GetTempPath()
                : startInfo.WorkingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        if (invocation.IsShellWrapped)
        {
            // cmd.exe wrapping requires the pre-escaped verbatim command line.
            psi.Arguments = invocation.VerbatimArguments;
        }
        else
        {
            foreach (var arg in invocation.ArgumentList)
                psi.ArgumentList.Add(arg);
        }

        if (startInfo.Environment is { Count: > 0 })
        {
            foreach (var (key, value) in startInfo.Environment)
            {
                if (value is null)
                    psi.Environment.Remove(key);
                else
                    psi.Environment[key] = value;
            }
        }

        var process = new System.Diagnostics.Process
        {
            StartInfo = psi,
            EnableRaisingEvents = true,
        };

        return new CliAgentProcessSession(process, startInfo.InactivityTimeout, cancellationToken);
    }
}
