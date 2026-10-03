using SelfClaw.Desktop.Services.Notifications;
using SelfClaw.Desktop.Services.Tools;
using System.IO;
using Microsoft.Extensions.Logging;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Core.Runtime.Agent;
using SelfClaw.Desktop.Services.Runtime;

namespace SelfClaw.Desktop.Services.Subagents;

internal sealed class SubagentContinuationExecutor
{
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan LeaseRenewalInterval = TimeSpan.FromSeconds(15);

    private readonly ISubagentDeliveryStore _deliveryStore;
    private readonly IAgentChatRuntime _chatRuntime;
    private readonly ConversationTurnRecorder _turnRecorder;
    private readonly DesktopToolApprovalHandler _approvalHandler;
    private readonly SubagentTaskSnapshotSerializer _snapshotSerializer;
    private readonly SubagentCompletionBatchSerializer _batchSerializer;
    private readonly ConversationRunCoordinator _runs;
    private readonly ConversationSessionCoordinator _sessions;
    private readonly DesktopNotificationService _notificationService;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<SubagentContinuationExecutor> _logger;

    public SubagentContinuationExecutor(
        ISubagentDeliveryStore deliveryStore,
        IAgentChatRuntime chatRuntime,
        ConversationTurnRecorder turnRecorder,
        DesktopToolApprovalHandler approvalHandler,
        SubagentTaskSnapshotSerializer snapshotSerializer,
        SubagentCompletionBatchSerializer batchSerializer,
        ConversationRunCoordinator runs,
        ConversationSessionCoordinator sessions,
        DesktopNotificationService notificationService,
        ILogger<SubagentContinuationExecutor> logger)
        : this(
            deliveryStore,
            chatRuntime,
            turnRecorder,
            approvalHandler,
            snapshotSerializer,
            batchSerializer,
            runs,
            sessions,
            notificationService,
            TimeProvider.System,
            logger)
    {
    }

    internal SubagentContinuationExecutor(
        ISubagentDeliveryStore deliveryStore,
        IAgentChatRuntime chatRuntime,
        ConversationTurnRecorder turnRecorder,
        DesktopToolApprovalHandler approvalHandler,
        SubagentTaskSnapshotSerializer snapshotSerializer,
        SubagentCompletionBatchSerializer batchSerializer,
        ConversationRunCoordinator runs,
        ConversationSessionCoordinator sessions,
        DesktopNotificationService notificationService,
        TimeProvider timeProvider,
        ILogger<SubagentContinuationExecutor> logger)
    {
        _deliveryStore = deliveryStore;
        _chatRuntime = chatRuntime;
        _turnRecorder = turnRecorder;
        _approvalHandler = approvalHandler;
        _snapshotSerializer = snapshotSerializer;
        _batchSerializer = batchSerializer;
        _runs = runs;
        _sessions = sessions;
        _notificationService = notificationService;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    internal async Task ExecuteAsync(
        ConversationRecord parentConversation,
        ConversationRunHandle handle,
        SubagentDeliveryLease lease,
        CancellationToken hostCancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parentConversation);
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentNullException.ThrowIfNull(lease);
        _runs.BeginExecution(handle);
        using var execution = CancellationTokenSource.CreateLinkedTokenSource(
            hostCancellationToken,
            handle.CancellationToken);
        using var heartbeatCancellation = CancellationTokenSource.CreateLinkedTokenSource(execution.Token);
        var leaseLost = 0;
        var heartbeat = RenewLeaseAsync(
            lease,
            heartbeatCancellation.Token,
            () =>
            {
                Interlocked.Exchange(ref leaseLost, 1);
                execution.Cancel();
            });
        ConversationRuntimeState? runtimeState = null;
        AgentTurnState? turn = null;
        SubagentContinuationTurnCommitter? committer = null;
        var publishPersistedTurn = false;
        var notifyDeadLetter = false;
        try
        {
            if (handle.Origin != DirectTurnOrigin.Continuation || handle.ConversationId != parentConversation.Id ||
                handle.ConversationId != lease.ParentConversationId || handle.TurnId != lease.ContinuationTurnId)
                throw new InvalidDataException("The continuation lease does not belong to this run.");
            runtimeState = await _sessions.PrepareRuntimeStateAsync(parentConversation, true, execution.Token);
            committer = new SubagentContinuationTurnCommitter(_deliveryStore, lease, _timeProvider);
            var record = new ConversationTurnRecord(handle.TurnId, handle.ConversationId, AgentExecutionMode.Direct,
                DirectTurnOrigin.Continuation, ConversationTurnStatus.Running, _timeProvider.GetUtcNow());
            runtimeState.ReplaceTurn(record);
            var request = CreateRequest(runtimeState, lease, committer);
            turn = new AgentTurnState(record, request.Agent);
            _turnRecorder.BeginTurn(runtimeState, turn);
            _runs.AttachState(handle, runtimeState);
            execution.Token.ThrowIfCancellationRequested();
            await foreach (var streamEvent in _chatRuntime.StreamTurnAsync(request, execution.Token))
            {
                await _turnRecorder.ApplyDetachedEventAsync(
                    runtimeState,
                    turn,
                    streamEvent,
                    committer,
                    execution.Token);
            }

            if (!turn.Completed && turn.PendingFinalization is null)
            {
                await _turnRecorder.FinalizeInterruptedAsync(
                    runtimeState,
                    turn,
                    TurnFinalizationKind.Failed,
                    "The continuation stream ended without a terminal event.",
                    committer);
            }

            (publishPersistedTurn, notifyDeadLetter) = ReadDisposition(committer);
        }
        catch (OperationCanceledException) when (Volatile.Read(ref leaseLost) != 0)
        {
            _logger.LogWarning(
                "Subagent continuation lease was lost. ParentConversationId={ParentConversationId} ContinuationTurnId={ContinuationTurnId}",
                lease.ParentConversationId,
                lease.ContinuationTurnId);
            throw;
        }
        catch (OperationCanceledException)
        {
            if (runtimeState is not null && turn is not null && committer is not null && turn.PendingFinalization is null)
            {
                await _turnRecorder.FinalizeInterruptedAsync(
                    runtimeState,
                    turn,
                    TurnFinalizationKind.Cancelled,
                    "The application stopped the Subagent continuation.",
                    committer);
                (publishPersistedTurn, notifyDeadLetter) = ReadDisposition(committer);
            }
            else if (turn is null)
            {
                await _deliveryStore.TryResolveAsync(lease,
                    new SubagentDeliveryResolution(SubagentDeliveryResolutionKind.RetryableFailure, null,
                        "The continuation was cancelled during preparation.", _timeProvider.GetUtcNow()));
            }
            throw;
        }
        catch (InvalidDataException exception)
        {
            _logger.LogError(
                exception,
                "Subagent continuation data is invalid. ParentConversationId={ParentConversationId} ContinuationTurnId={ContinuationTurnId}",
                lease.ParentConversationId,
                lease.ContinuationTurnId);
            var result = await _deliveryStore.TryResolveAsync(
                lease,
                new SubagentDeliveryResolution(
                    SubagentDeliveryResolutionKind.DeadLetter,
                    null,
                    exception.Message,
                    _timeProvider.GetUtcNow()));
            notifyDeadLetter = result.DeadLetteredDeliveryIds.Count > 0;
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "Subagent continuation failed. ParentConversationId={ParentConversationId} ContinuationTurnId={ContinuationTurnId}",
                lease.ParentConversationId,
                lease.ContinuationTurnId);
            if (runtimeState is not null && turn is not null && committer is not null && turn.PendingFinalization is null)
            {
                await _turnRecorder.FinalizeInterruptedAsync(
                    runtimeState,
                    turn,
                    TurnFinalizationKind.Failed,
                    exception.Message,
                    committer);
                (publishPersistedTurn, notifyDeadLetter) = ReadDisposition(committer);
            }
            else if (turn is null)
            {
                var result = await _deliveryStore.TryResolveAsync(
                    lease,
                    new SubagentDeliveryResolution(
                        SubagentDeliveryResolutionKind.RetryableFailure,
                        null,
                        exception.Message,
                        _timeProvider.GetUtcNow()));
                notifyDeadLetter = result.DeadLetteredDeliveryIds.Count > 0;
            }
        }
        finally
        {
            try
            {
                heartbeatCancellation.Cancel();
                await AwaitHeartbeatAsync(heartbeat);
            }
            finally
            {
                if (committer is { Disposition: not SubagentContinuationDisposition.None })
                    (publishPersistedTurn, notifyDeadLetter) = ReadDisposition(committer);
                _runs.Complete(handle, publishPersistedTurn);
            }
        }

        if (notifyDeadLetter)
        {
            _notificationService.ShowSubagentContinuationFailed(
                parentConversation.Id,
                parentConversation.Title,
                committer?.BlockedReason ??
                "Subagent results could not be continued safely. Open the conversation for details.");
        }
    }

    private DirectChatTurnRequest CreateRequest(
        ConversationRuntimeState runtimeState,
        SubagentDeliveryLease lease,
        IToolExecutionCheckpoint checkpoint)
    {
        var parent = _snapshotSerializer.DeserializeParent(lease.ParentExecutionSnapshotJson);
        if (parent.Version != 1 || parent.ModelProfileId == Guid.Empty)
        {
            throw new InvalidDataException("The parent execution snapshot is invalid.");
        }

        var batch = _batchSerializer.Deserialize(lease);
        return new DirectChatTurnRequest(
            lease.ContinuationTurnId,
            lease.ParentConversationId,
            parent.WorkspaceRoot,
            parent.Agent,
            runtimeState.Messages.ToArray(),
            runtimeState.Turns.ToArray(),
            parent.ModelProfileId,
            parent.ToolPermissionMode,
            _approvalHandler,
            new DirectTurnExecutionContext(
                DirectTurnOrigin.Continuation,
                parent.CapabilityCeiling,
                batch),
            runtimeState.ToolRuns.ToArray(),
            checkpoint);
    }

    private async Task RenewLeaseAsync(
        SubagentDeliveryLease lease,
        CancellationToken cancellationToken,
        Action leaseLost)
    {
        using var timer = new PeriodicTimer(LeaseRenewalInterval, _timeProvider);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                var now = _timeProvider.GetUtcNow();
                var renewed = await _deliveryStore.TryRenewLeaseAsync(
                    lease,
                    now,
                    now + LeaseDuration,
                    cancellationToken);
                if (!renewed)
                {
                    leaseLost();
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed to renew the continuation lease; stopping execution.");
            leaseLost();
        }
    }

    private static async Task AwaitHeartbeatAsync(Task heartbeat)
    {
        try
        {
            await heartbeat;
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static (bool Publish, bool NotifyDeadLetter) ReadDisposition(
        SubagentContinuationTurnCommitter committer)
    {
        var deadLetter = committer.Disposition == SubagentContinuationDisposition.DeadLetter;
        var publish = committer.Disposition == SubagentContinuationDisposition.Delivered ||
                      (deadLetter && committer.ToolsMayHaveExecuted);
        return (publish, deadLetter);
    }
}
