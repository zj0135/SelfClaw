using System.Diagnostics;
using System.IO;
using System.Text;
using SelfClaw.Desktop.Services.ProgrammingAssistant.Models;
using SelfClaw.Infrastructure.Agents.Cli.Process.Models;

namespace SelfClaw.Desktop.Services.ProgrammingAssistant;

internal sealed class CliProbeProcess
{
    internal const int MaximumOutputCharacters = 262144;

    public async Task<CliProbeOutput> RunAsync(CommandInvocation invocation, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        cancellationToken.ThrowIfCancellationRequested();
        using var process = CreateProcess(invocation);
        if (!process.Start()) throw new InvalidOperationException("The CLI probe could not start.");
        using var drainCancellation = new CancellationTokenSource();
        var stdout = DrainAsync(process.StandardOutput, drainCancellation.Token);
        var stderr = DrainAsync(process.StandardError, drainCancellation.Token);
        var drains = Task.WhenAll(stdout, stderr);
        try
        {
            await Task.WhenAll(process.WaitForExitAsync(cancellationToken), drains).WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            var output = await stdout.ConfigureAwait(false);
            var error = await stderr.ConfigureAwait(false);
            return new(process.ExitCode, output.Text, error.Text, output.Truncated || error.Truncated);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            drainCancellation.CancelAfter(TimeSpan.FromSeconds(2));
            try { await drains.ConfigureAwait(false); }
            catch (OperationCanceledException) when (drainCancellation.IsCancellationRequested)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
        }
    }

    private static async Task<(string Text, bool Truncated)> DrainAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var output = new StringBuilder();
        var buffer = new char[8192];
        var truncated = false;
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
        {
            var retained = Math.Min(count, MaximumOutputCharacters - output.Length);
            output.Append(buffer, 0, retained);
            truncated |= retained != count;
        }
        if (truncated && output.Length > 0 && char.IsHighSurrogate(output[^1])) output.Length--;
        return (output.ToString(), truncated);
    }

    private static Process CreateProcess(CommandInvocation invocation)
    {
        var start = new ProcessStartInfo { FileName = invocation.FileName, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8 };
        if (invocation.IsShellWrapped) start.Arguments = invocation.VerbatimArguments;
        else foreach (var argument in invocation.ArgumentList) start.ArgumentList.Add(argument);
        return new Process { StartInfo = start };
    }
}
