namespace SelfClaw.Infrastructure.Agents.Cli.Process;

/// <summary>
/// A running CLI agent turn. Callers write the JSONL prompt with
/// <see cref="WriteStdinLineAsync"/> (or <see cref="WriteStdinAsync"/>), call
/// <see cref="CompleteStdinAsync"/> to close stdin and signal EOF, consume stdout through
/// <see cref="ReadOutputLinesAsync"/>, then await <see cref="WaitForExitAsync"/> for the classified
/// outcome. Disposing the session kills the process if it is still alive.
/// </summary>
public interface ICliAgentProcessSession : IAsyncDisposable
{
    /// <summary>Writes <paramref name="text"/> to the child's stdin verbatim (no trailing newline).</summary>
    Task WriteStdinAsync(string text, CancellationToken cancellationToken = default);

    /// <summary>Writes a single JSONL record followed by <c>'\n'</c> to the child's stdin.</summary>
    Task WriteStdinLineAsync(string line, CancellationToken cancellationToken = default);

    /// <summary>Flushes and closes stdin, signalling end-of-turn (EOF) to the child.</summary>
    Task CompleteStdinAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Streams stdout lines (newline-delimited, without the terminator) until the process exits.
    /// The inactivity watchdog resets on each yielded line.
    /// </summary>
    IAsyncEnumerable<string> ReadOutputLinesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Waits for the process to exit and returns its classified outcome. Safe to call after the
    /// output stream completes; returns the same result on repeated calls.
    /// </summary>
    Task<CliProcessResult> WaitForExitAsync(CancellationToken cancellationToken = default);
}
