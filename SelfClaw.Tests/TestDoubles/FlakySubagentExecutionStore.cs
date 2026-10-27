using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;

namespace SelfClaw.Tests.TestDoubles;

/// <summary>
/// Fails a fixed number of consecutive claims to reproduce a transient storage error, so the
/// background host's retry path can be exercised without a real SQLite fault.
/// </summary>
internal sealed class FlakySubagentExecutionStore(ISubagentTaskExecutionStore inner) : ISubagentTaskExecutionStore
{
    private int _remainingFailures;
    private int _claimAttempts;

    internal FlakySubagentExecutionStore FailNextClaims(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        _remainingFailures = count;
        return this;
    }

    internal int ClaimAttempts => Volatile.Read(ref _claimAttempts);

    public Task<IReadOnlyList<SubagentTaskRecord>> ListByStatusAsync(
        SubagentTaskStatus status,
        CancellationToken cancellationToken = default)
        => inner.ListByStatusAsync(status, cancellationToken);

    public Task<SubagentTaskRecord?> TryClaimNextAsync(DateTimeOffset startedAtUtc, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _claimAttempts);
        if (Interlocked.Decrement(ref _remainingFailures) >= 0)
        {
            throw new InvalidOperationException("SQLite Error 1: 'SQL logic error'");
        }

        return inner.TryClaimNextAsync(startedAtUtc, cancellationToken);
    }

    public Task<SubagentTaskRecord?> TryCompleteAsync(Guid taskId, SubagentTaskStatus expectedStatus,
        SubagentTaskCompletion completion, CancellationToken cancellationToken = default)
        => inner.TryCompleteAsync(taskId, expectedStatus, completion, cancellationToken);
}
