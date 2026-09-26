using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Desktop.Services.Runtime;

namespace SelfClaw.Desktop.Services.Subagents;

internal sealed class SubagentContinuationTurnCommitter : IRecordedTurnCommitter, IToolExecutionCheckpoint
{
    private readonly ISubagentDeliveryStore _deliveryStore;
    private readonly SubagentDeliveryLease _lease;
    private readonly TimeProvider _timeProvider;

    internal SubagentContinuationTurnCommitter(
        ISubagentDeliveryStore deliveryStore,
        SubagentDeliveryLease lease,
        TimeProvider timeProvider)
    {
        _deliveryStore = deliveryStore;
        _lease = lease;
        _timeProvider = timeProvider;
    }

    internal SubagentContinuationDisposition Disposition { get; private set; }
    internal bool ToolsMayHaveExecuted { get; private set; }

    /// <summary>
    /// Set when the continuation was blocked by a Plugin hook. The same hook would block a retry, so
    /// the delivery is dead-lettered instead of retried.
    /// </summary>
    internal string? BlockedReason { get; private set; }

    internal void MarkBlocked(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        BlockedReason = reason;
    }

    public async Task BeforeExecutionAsync(CancellationToken cancellationToken)
    {
        if (!await _deliveryStore.TryMarkToolExecutionStartedAsync(_lease, _timeProvider.GetUtcNow(), cancellationToken))
        {
            throw new InvalidOperationException("The continuation no longer owns its delivery lease; tool execution was blocked.");
        }

        ToolsMayHaveExecuted = true;
    }

    public async Task<bool> TryCommitAsync(RecordedTurnCommit commit)
    {
        ArgumentNullException.ThrowIfNull(commit);
        var resolutionKind = commit.Kind switch
        {
            TurnFinalizationKind.Succeeded => SubagentDeliveryResolutionKind.Succeeded,
            // UnsafeFailure also resolves to DeadLetter, so a blocked run that already executed tools
            // can persist its terminal record through the only kind the store accepts one for.
            _ when ToolsMayHaveExecuted => SubagentDeliveryResolutionKind.UnsafeFailure,
            _ when BlockedReason is not null => SubagentDeliveryResolutionKind.DeadLetter,
            _ => SubagentDeliveryResolutionKind.RetryableFailure
        };
        var persistsFinalization = resolutionKind is SubagentDeliveryResolutionKind.Succeeded
            or SubagentDeliveryResolutionKind.UnsafeFailure;
        var result = await _deliveryStore.TryResolveAsync(
            _lease,
            new SubagentDeliveryResolution(
                resolutionKind,
                persistsFinalization ? commit.Finalization : null,
                commit.ErrorMessage,
                _timeProvider.GetUtcNow()));
        if (!result.LeaseMatched)
        {
            Disposition = SubagentContinuationDisposition.LeaseLost;
            return false;
        }

        Disposition = result.DeliveredDeliveryIds.Count > 0
            ? SubagentContinuationDisposition.Delivered
            : result.DeadLetteredDeliveryIds.Count > 0
                ? SubagentContinuationDisposition.DeadLetter
                : SubagentContinuationDisposition.Retrying;
        return true;
    }
}
