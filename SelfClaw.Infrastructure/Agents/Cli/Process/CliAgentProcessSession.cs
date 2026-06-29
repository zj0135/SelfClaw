using System.Diagnostics;
using System.Text;
using System.Threading.Channels;
using SelfClaw.Core.Runtime.Agent;
using SelfClaw.Infrastructure.Agents.Cli.Process.Abstractions;
using SelfClaw.Infrastructure.Agents.Cli.Process.Models;

namespace SelfClaw.Infrastructure.Agents.Cli.Process;

/// <summary>
/// A single CLI agent turn backed by a live <see cref="System.Diagnostics.Process"/>.
/// <para>
/// Lifecycle (plan.md §5): the caller writes the JSONL prompt via <see cref="WriteStdinLineAsync"/>,
/// closes stdin with <see cref="CompleteStdinAsync"/> to signal EOF, drains stdout through
/// <see cref="ReadOutputLinesAsync"/>, then awaits <see cref="WaitForExitAsync"/>. stdout lines are
/// pumped into a bounded-less <see cref="Channel{T}"/> by a background reader so the inactivity
/// watchdog and cancellation can act independently of how fast the caller consumes them. stderr is
/// captured into a capped buffer for diagnostics. The process is killed on cancellation, on the
/// no-activity timeout, or when the session is disposed while still running.
/// </para>
/// </summary>
internal sealed class CliAgentProcessSession : ICliAgentProcessSession
{
    // Cap stderr retention so a chatty/looping child cannot exhaust memory; diagnostics only.
    private const int MaxStandardErrorChars = 64 * 1024;

    private readonly System.Diagnostics.Process _process;
    private readonly TimeSpan _inactivityTimeout;
    private readonly CancellationTokenSource _linkedCts;
    private readonly CancellationToken _externalToken;
    private readonly Channel<string> _outputChannel;
    private readonly StringBuilder _standardError = new();
    private readonly object _stderrLock = new();
    private readonly TaskCompletionSource<CliProcessResult> _exitSource =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly Task _stdoutPump;
    private readonly Task _stderrPump;

    // Tracks the last time the child produced any output; the watchdog kills it once idle too long.
    private long _lastActivityTicks;
    private Timer? _watchdog;
    private int _disposed;
    private volatile bool _killedForInactivity;
    private volatile bool _started;

    public CliAgentProcessSession(
        System.Diagnostics.Process process,
        TimeSpan inactivityTimeout,
        CancellationToken cancellationToken)
    {
        _process = process;
        _inactivityTimeout = inactivityTimeout;
        _externalToken = cancellationToken;
        _linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _outputChannel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true,
        });

        if (!_process.Start())
            throw new InvalidOperationException("Failed to start the CLI agent process.");

        _started = true;
        MarkActivity();
        StartWatchdog();

        _stdoutPump = Task.Run(PumpStandardOutputAsync);
        _stderrPump = Task.Run(PumpStandardErrorAsync);
    }

    public async Task WriteStdinAsync(string text, CancellationToken cancellationToken = default)
    {
        using var linked = Link(cancellationToken);
        await _process.StandardInput.WriteAsync(text.AsMemory(), linked.Token).ConfigureAwait(false);
        await _process.StandardInput.FlushAsync(linked.Token).ConfigureAwait(false);
    }

    public async Task WriteStdinLineAsync(string line, CancellationToken cancellationToken = default)
    {
        using var linked = Link(cancellationToken);
        await _process.StandardInput.WriteAsync(line.AsMemory(), linked.Token).ConfigureAwait(false);
        await _process.StandardInput.WriteAsync("\n".AsMemory(), linked.Token).ConfigureAwait(false);
        await _process.StandardInput.FlushAsync(linked.Token).ConfigureAwait(false);
    }

    public async Task CompleteStdinAsync(CancellationToken cancellationToken = default)
    {
        using var linked = Link(cancellationToken);
        await _process.StandardInput.FlushAsync(linked.Token).ConfigureAwait(false);
        _process.StandardInput.Close();
    }

    public IAsyncEnumerable<string> ReadOutputLinesAsync(CancellationToken cancellationToken = default) =>
        _outputChannel.Reader.ReadAllAsync(cancellationToken);

    public Task<CliProcessResult> WaitForExitAsync(CancellationToken cancellationToken = default) =>
        _exitSource.Task.WaitAsync(cancellationToken);

    private async Task PumpStandardOutputAsync()
    {
        try
        {
            string? line;
            while ((line = await _process.StandardOutput.ReadLineAsync(_linkedCts.Token).ConfigureAwait(false)) is not null)
            {
                MarkActivity();
                await _outputChannel.Writer.WriteAsync(line, _linkedCts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Cancellation or inactivity kill — completion is reported via the exit classification.
        }
        catch (Exception ex)
        {
            _outputChannel.Writer.TryComplete(ex);
            await FinishAsync().ConfigureAwait(false);
            return;
        }

        _outputChannel.Writer.TryComplete();
        await FinishAsync().ConfigureAwait(false);
    }

    private async Task PumpStandardErrorAsync()
    {
        try
        {
            string? line;
            while ((line = await _process.StandardError.ReadLineAsync(_linkedCts.Token).ConfigureAwait(false)) is not null)
            {
                MarkActivity();
                AppendStandardError(line);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on cancellation / inactivity kill.
        }
        catch
        {
            // stderr is best-effort diagnostics; never let it fault the turn.
        }
    }

    /// <summary>
    /// Waits for the process to fully exit, classifies the outcome, and completes the exit task.
    /// Invoked once the stdout pump drains; idempotent via <see cref="_exitSource"/>.
    /// </summary>
    private async Task FinishAsync()
    {
        try
        {
            await _process.WaitForExitAsync().ConfigureAwait(false);
        }
        catch
        {
            // Process may already be gone; fall through to classification.
        }

        StopWatchdog();

        int? exitCode = null;
        try
        {
            exitCode = _process.ExitCode;
        }
        catch
        {
            // ExitCode throws if the process never started cleanly.
        }

        var status = ClassifyExit(exitCode);
        var result = new CliProcessResult(status, exitCode, ReadStandardError())
        {
            TimedOut = _killedForInactivity,
        };

        _exitSource.TrySetResult(result);
    }

    /// <summary>
    /// Maps exit state to <see cref="RunCompletionStatus"/> per plan.md §5: cancellation (external or
    /// inactivity kill) → Canceled/Failed, exit 0 → Succeeded, anything else → Failed.
    /// </summary>
    private RunCompletionStatus ClassifyExit(int? exitCode)
    {
        if (_killedForInactivity)
            return RunCompletionStatus.Failed;
        if (_externalToken.IsCancellationRequested)
            return RunCompletionStatus.Canceled;
        if (exitCode == 0)
            return RunCompletionStatus.Succeeded;
        return RunCompletionStatus.Failed;
    }

    private void StartWatchdog()
    {
        if (_inactivityTimeout <= TimeSpan.Zero || _inactivityTimeout == Timeout.InfiniteTimeSpan)
            return;

        // Poll at a fraction of the window so the kill lands within ~10% of the deadline.
        var pollInterval = TimeSpan.FromMilliseconds(
            Math.Max(250, _inactivityTimeout.TotalMilliseconds / 10));
        _watchdog = new Timer(_ => CheckInactivity(), null, pollInterval, pollInterval);
    }

    private void CheckInactivity()
    {
        var last = Interlocked.Read(ref _lastActivityTicks);
        var idle = TimeSpan.FromTicks(DateTime.UtcNow.Ticks - last);
        if (idle < _inactivityTimeout)
            return;

        _killedForInactivity = true;
        KillProcessTree();
    }

    private void MarkActivity() =>
        Interlocked.Exchange(ref _lastActivityTicks, DateTime.UtcNow.Ticks);

    private void StopWatchdog()
    {
        var watchdog = Interlocked.Exchange(ref _watchdog, null);
        watchdog?.Dispose();
    }

    private void KillProcessTree()
    {
        try
        {
            if (!_process.HasExited)
                _process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Race with natural exit; nothing to do.
        }
    }

    private void AppendStandardError(string line)
    {
        lock (_stderrLock)
        {
            if (_standardError.Length >= MaxStandardErrorChars)
                return;
            _standardError.Append(line).Append('\n');
        }
    }

    private string? ReadStandardError()
    {
        lock (_stderrLock)
        {
            return _standardError.Length == 0 ? null : _standardError.ToString().TrimEnd('\n');
        }
    }

    private CancellationTokenSource Link(CancellationToken token) =>
        CancellationTokenSource.CreateLinkedTokenSource(_linkedCts.Token, token);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        StopWatchdog();

        // Signal the pumps to unwind, then kill the child if it is still running.
        try
        {
            _linkedCts.Cancel();
        }
        catch
        {
            // CTS already disposed.
        }

        if (_started)
            KillProcessTree();

        try
        {
            await Task.WhenAll(_stdoutPump, _stderrPump).ConfigureAwait(false);
        }
        catch
        {
            // Pumps observe cancellation; swallow on teardown.
        }

        // Ensure callers awaiting exit are released even if the pump never completed it.
        _exitSource.TrySetResult(new CliProcessResult(
            ClassifyExit(TryGetExitCode()), TryGetExitCode(), ReadStandardError())
        {
            TimedOut = _killedForInactivity,
        });

        _linkedCts.Dispose();

        try
        {
            _process.Dispose();
        }
        catch
        {
            // Ignore disposal races.
        }
    }

    private int? TryGetExitCode()
    {
        try
        {
            return _process.HasExited ? _process.ExitCode : null;
        }
        catch
        {
            return null;
        }
    }
}
