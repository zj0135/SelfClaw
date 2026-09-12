using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;

namespace SelfClaw.Tests.TestDoubles;

internal sealed class InterceptingSubagentExecutionStore(ISubagentTaskExecutionStore inner) : ISubagentTaskExecutionStore
{
    internal Func<Task>? BeforeCompleteAsync { get; set; }

    public Task<IReadOnlyList<SubagentTaskRecord>> ListByStatusAsync(SubagentTaskStatus status, CancellationToken cancellationToken = default)
        => inner.ListByStatusAsync(status, cancellationToken);

    public Task<SubagentTaskRecord?> TryClaimNextAsync(DateTimeOffset startedAtUtc, CancellationToken cancellationToken = default)
        => inner.TryClaimNextAsync(startedAtUtc, cancellationToken);

    public async Task<SubagentTaskRecord?> TryCompleteAsync(Guid taskId, SubagentTaskStatus expectedStatus,
        SubagentTaskCompletion completion, CancellationToken cancellationToken = default)
    {
        if (BeforeCompleteAsync is { } beforeComplete)
        {
            await beforeComplete();
        }

        return await inner.TryCompleteAsync(taskId, expectedStatus, completion, cancellationToken);
    }
}
