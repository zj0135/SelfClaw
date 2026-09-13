using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;

namespace SelfClaw.Tests.TestDoubles;

internal sealed class InterceptingSubagentDeliveryStore(ISubagentDeliveryStore inner) : ISubagentDeliveryStore
{
    internal Exception? LeaseFailure { get; set; }
    internal Guid? LeaseFailureParentId { get; set; }
    internal bool ReturnEmptyLease { get; set; }
    internal Action? AfterLease { get; set; }
    internal int Resolutions { get; private set; }

    public Task<SubagentMailboxKey?> PeekReadyMailboxAsync(DateTimeOffset readyAtUtc, DateTimeOffset createdBeforeUtc,
        CancellationToken cancellationToken = default, IReadOnlyCollection<Guid>? excludedParentConversationIds = null)
        => inner.PeekReadyMailboxAsync(readyAtUtc, createdBeforeUtc, cancellationToken, excludedParentConversationIds);

    public async Task<SubagentDeliveryLease?> TryLeaseBatchAsync(SubagentMailboxKey mailbox, Guid leaseToken,
        Guid continuationTurnId, DateTimeOffset leasedAtUtc, DateTimeOffset leasedUntilUtc, int maximumBatchBytes,
        CancellationToken cancellationToken = default)
    {
        if (LeaseFailure is not null && (LeaseFailureParentId is null || LeaseFailureParentId == mailbox.ParentConversationId)) throw LeaseFailure;
        if (ReturnEmptyLease) return null;
        var lease = await inner.TryLeaseBatchAsync(mailbox, leaseToken, continuationTurnId, leasedAtUtc, leasedUntilUtc,
            maximumBatchBytes, cancellationToken);
        AfterLease?.Invoke();
        return lease;
    }

    public Task<bool> TryRenewLeaseAsync(SubagentDeliveryLease lease, DateTimeOffset renewedAtUtc, DateTimeOffset leasedUntilUtc,
        CancellationToken cancellationToken = default) => inner.TryRenewLeaseAsync(lease, renewedAtUtc, leasedUntilUtc, cancellationToken);

    public Task<bool> TryMarkToolExecutionStartedAsync(SubagentDeliveryLease lease, DateTimeOffset startedAtUtc,
        CancellationToken cancellationToken = default) => inner.TryMarkToolExecutionStartedAsync(lease, startedAtUtc, cancellationToken);

    public Task<SubagentDeliveryResolutionResult> TryResolveAsync(SubagentDeliveryLease lease, SubagentDeliveryResolution resolution,
        CancellationToken cancellationToken = default)
    {
        Resolutions++;
        return inner.TryResolveAsync(lease, resolution, cancellationToken);
    }

    public Task<IReadOnlyList<SubagentDeliveryRecord>> RecoverExpiredLeasesAsync(DateTimeOffset recoveredAtUtc,
        CancellationToken cancellationToken = default) => inner.RecoverExpiredLeasesAsync(recoveredAtUtc, cancellationToken);
}
