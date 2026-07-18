using System.Diagnostics;
using System.Text;
using SelfClaw.Infrastructure.Agents.Cli.Process.Abstractions;
using SelfClaw.Infrastructure.Agents.Cli.Process.Models;

namespace SelfClaw.Infrastructure.Agents.Cli.Process;

/// <summary>
/// Default <see cref="ICliAgentProcessHost"/> backed by <see cref="System.Diagnostics.Process"/>.
/// Launches a CLI agent subprocess with stdin/stdout/stderr redirected (plan.md §5) and hands the
/// caller a <see cref="CliAgentProcessSession"/> that owns the turn lifecycle.
/// </summary>
public sealed class CliAgentProcessHost : ICliAgentProcessHost
{
    // UTF-8 without a BOM: the BOM would corrupt the first stdin JSONL line the child reads.
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

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
            // The CLI emits UTF-8 JSONL. Without these the child's output is decoded with the OS
            // default code page (e.g. GBK on Chinese Windows), mangling any non-ASCII text. Use a
            // BOM-less UTF-8 so the prompt written to stdin is not prefixed with a BOM either.
            StandardOutputEncoding = Utf8NoBom,
            StandardErrorEncoding = Utf8NoBom,
            StandardInputEncoding = Utf8NoBom,
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

        var process = new System.Diagnostics.Process
        {
            StartInfo = psi,
            EnableRaisingEvents = true,
        };

        return new CliAgentProcessSession(process, startInfo.InactivityTimeout, cancellationToken);
    }
}
