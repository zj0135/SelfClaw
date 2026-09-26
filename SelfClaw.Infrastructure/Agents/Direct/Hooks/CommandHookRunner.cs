using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SelfClaw.Infrastructure.Agents.Direct.Hooks.Models;
using SelfClaw.Infrastructure.Processes;

namespace SelfClaw.Infrastructure.Agents.Direct.Hooks;

/// <summary>
/// Runs one hook command process: stdin gets a single payload document, stdout is the decision, and
/// the whole tree is confined to a Job Object so a hook cannot leave background processes behind.
/// Template expansion, decision parsing and the execution log are the caller's responsibility.
/// </summary>
internal sealed class CommandHookRunner
{
    private const int StdoutBufferSize = 8192;
    private const int MaximumStderrCharacters = 8 * 1024;
    private static readonly TimeSpan PipeDrainWait = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan CancellationExitWait = TimeSpan.FromSeconds(2);
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly ILogger<CommandHookRunner> _logger;

    public CommandHookRunner(ILogger<CommandHookRunner>? logger = null)
    {
        _logger = logger ?? NullLogger<CommandHookRunner>.Instance;
    }

    public async Task<HookProcessResult> RunAsync(
        HookProcessStart start,
        ReadOnlyMemory<byte> input,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(start);
        cancellationToken.ThrowIfCancellationRequested();
        var startInfo = CreateStartInfo(start);
        var stopwatch = Stopwatch.StartNew();
        Process? process = null;
        try
        {
            try
            {
                process = Process.Start(startInfo);
            }
            catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or PlatformNotSupportedException)
            {
                return new HookProcessResult(
                    HookProcessExit.LaunchFailed, null, string.Empty, string.Empty, stopwatch.Elapsed, exception.Message);
            }

            if (process is null)
            {
                return new HookProcessResult(
                    HookProcessExit.LaunchFailed, null, string.Empty, string.Empty, stopwatch.Elapsed,
                    "The hook process did not start.");
            }

            using var job = ProcessJob.TryCreate();
            if (job is not null)
            {
                if (!job.TryAssign(process))
                {
                    _logger.LogWarning(
                        "Failed to assign hook process {ProcessId} to a Job Object; falling back to process-tree kill.",
                        process.Id);
                }
            }
            else
            {
                _logger.LogWarning(
                    "Failed to create a Job Object for hook process {ProcessId}; falling back to process-tree kill.",
                    process.Id);
            }

            return await RunProcessAsync(process, job, input, timeout, stopwatch, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            process?.Dispose();
        }
    }

    private async Task<HookProcessResult> RunProcessAsync(
        Process process,
        ProcessJob? job,
        ReadOnlyMemory<byte> input,
        TimeSpan timeout,
        Stopwatch stopwatch,
        CancellationToken cancellationToken)
    {
        using var pipes = new CancellationTokenSource();
        var stdoutBuffer = new MemoryStream();
        var stderr = new StringBuilder();
        var overflow = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stdinTask = WriteInputAsync(process, input, pipes.Token);
        var stdoutTask = ReadStdoutAsync(process.StandardOutput.BaseStream, stdoutBuffer, overflow, pipes.Token);
        var stderrTask = ReadStderrAsync(process.StandardError.BaseStream, stderr, pipes.Token);
        var exitTask = process.WaitForExitAsync(CancellationToken.None);
        var timeoutTask = Task.Delay(timeout, CancellationToken.None);

        var winner = await Task.WhenAny(exitTask, timeoutTask, overflow.Task).ConfigureAwait(false);
        var pipesToDrain = new[] { stdinTask, stdoutTask, stderrTask };
        if (winner != exitTask || cancellationToken.IsCancellationRequested)
        {
            Terminate(process, job);
            await DrainAsync(pipesToDrain, CancellationExitWait).ConfigureAwait(false);
            pipes.Cancel();
            await DrainAsync(pipesToDrain, TimeSpan.Zero).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return winner == overflow.Task
                ? new HookProcessResult(
                    HookProcessExit.OutputTooLarge, null, string.Empty, stderr.ToString(), stopwatch.Elapsed,
                    $"Hook stdout exceeded {HookProtocol.MaximumOutputBytes} bytes.")
                : new HookProcessResult(
                    HookProcessExit.TimedOut, null, string.Empty, stderr.ToString(), stopwatch.Elapsed,
                    $"Hook timed out after {timeout.TotalSeconds:0.###} seconds.");
        }

        // The main process exited, but it may have left descendants that still hold the pipes
        // (or keep running). Terminating the job closes both at once; the pipes then get a bounded
        // window to drain before their content is abandoned.
        Terminate(process, job);
        await DrainAsync(pipesToDrain, PipeDrainWait).ConfigureAwait(false);
        pipes.Cancel();
        await DrainAsync(pipesToDrain, TimeSpan.Zero).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var exitCode = TryGetExitCode(process);
        return new HookProcessResult(
            HookProcessExit.Exited,
            exitCode,
            Utf8NoBom.GetString(stdoutBuffer.ToArray()),
            stderr.ToString(),
            stopwatch.Elapsed,
            null);
    }

    private static ProcessStartInfo CreateStartInfo(HookProcessStart start)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = start.FileName,
            WorkingDirectory = start.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = Utf8NoBom,
            StandardOutputEncoding = Utf8NoBom,
            StandardErrorEncoding = Utf8NoBom
        };
        foreach (var argument in start.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        // The inherited environment would otherwise make "no workspace root" indistinguishable from
        // an inherited SELFCLAW_WORKSPACE_ROOT, so the host's own variables are dropped first.
        foreach (var name in startInfo.Environment.Keys
                     .Where(key => key.StartsWith("SELFCLAW_", StringComparison.OrdinalIgnoreCase))
                     .ToArray())
        {
            startInfo.Environment.Remove(name);
        }

        foreach (var pair in start.Environment)
        {
            startInfo.Environment[pair.Key] = pair.Value;
        }

        return startInfo;
    }

    private static async Task WriteInputAsync(Process process, ReadOnlyMemory<byte> input, CancellationToken token)
    {
        try
        {
            var stream = process.StandardInput.BaseStream;
            await stream.WriteAsync(input, token).ConfigureAwait(false);
            await stream.FlushAsync(token).ConfigureAwait(false);
            await stream.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // The hook exited before reading stdin; the exit code is still authoritative.
        }
    }

    private static async Task ReadStdoutAsync(
        Stream stream,
        MemoryStream sink,
        TaskCompletionSource overflow,
        CancellationToken token)
    {
        var buffer = new byte[StdoutBufferSize];
        long total = 0;
        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(buffer, token).ConfigureAwait(false);
                if (read == 0)
                {
                    return;
                }

                total += read;
                if (total > HookProtocol.MaximumOutputBytes)
                {
                    overflow.TrySetResult();
                    return;
                }

                sink.Write(buffer, 0, read);
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or OperationCanceledException)
        {
        }
    }

    private static async Task ReadStderrAsync(Stream stream, StringBuilder sink, CancellationToken token)
    {
        var buffer = new byte[StdoutBufferSize];
        var decoder = Utf8NoBom.GetDecoder();
        var characters = new char[StdoutBufferSize];
        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(buffer, token).ConfigureAwait(false);
                if (read == 0)
                {
                    return;
                }

                var count = decoder.GetChars(buffer, 0, read, characters, 0);
                sink.Append(characters, 0, count);
                if (sink.Length > MaximumStderrCharacters)
                {
                    sink.Remove(0, sink.Length - MaximumStderrCharacters);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or OperationCanceledException)
        {
        }
    }

    private static void Terminate(Process process, ProcessJob? job)
    {
        if (job is not null)
        {
            job.Terminate();
        }

        ProcessTree.TryKill(process);
    }

    private static async Task DrainAsync(IReadOnlyList<Task> tasks, TimeSpan wait)
    {
        var all = Task.WhenAll(tasks);
        if (wait <= TimeSpan.Zero)
        {
            await all.ConfigureAwait(false);
            return;
        }

        await Task.WhenAny(all, Task.Delay(wait)).ConfigureAwait(false);
    }

    private static int? TryGetExitCode(Process process)
    {
        try
        {
            return process.ExitCode;
        }
        catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException)
        {
            return null;
        }
    }
}
