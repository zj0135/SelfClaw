using SelfClaw.Core.Models;

namespace SelfClaw.Core.Interfaces;

public interface ISubagentDeliveryStore
{
    Task<SubagentMailboxKey?> PeekReadyMailboxAsync(
        DateTimeOffset readyAtUtc,
        DateTimeOffset createdBeforeUtc,
        CancellationToken cancellationToken = default);

    Task<SubagentDeliveryLease?> TryLeaseBatchAsync(
        SubagentMailboxKey mailbox,
        Guid leaseToken,
        Guid continuationTurnId,
        DateTimeOffset leasedAtUtc,
        DateTimeOffset leasedUntilUtc,
        int maximumBatchBytes,
        CancellationToken cancellationToken = default);

    Task<bool> TryRenewLeaseAsync(
        SubagentDeliveryLease lease,
        DateTimeOffset renewedAtUtc,
        DateTimeOffset leasedUntilUtc,
        CancellationToken cancellationToken = default);

    Task<SubagentDeliveryResolutionResult> TryResolveAsync(
        SubagentDeliveryLease lease,
        SubagentDeliveryResolution resolution,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SubagentDeliveryRecord>> RecoverExpiredLeasesAsync(
        DateTimeOffset recoveredAtUtc,
        CancellationToken cancellationToken = default);
}
