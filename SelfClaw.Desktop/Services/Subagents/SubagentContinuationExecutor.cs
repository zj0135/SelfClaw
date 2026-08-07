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
    private readonly ConversationTurnEngine _turnEngine;
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
        ConversationTurnEngine turnEngine,
        DesktopNotificationService notificationService,
        ILogger<SubagentContinuationExecutor> logger)
        : this(
            deliveryStore,
            chatRuntime,
            turnRecorder,
            approvalHandler,
            snapshotSerializer,
            batchSerializer,
            turnEngine,
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
        ConversationTurnEngine turnEngine,
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
        _turnEngine = turnEngine;
        _notificationService = notificationService;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    internal async Task ExecuteAsync(
        ConversationRecord parentConversation,
        ConversationRuntimeState runtimeState,
        SubagentDeliveryLease lease,
        CancellationToken hostCancellationToken)
    {
        ArgumentNullException.ThrowIfNull(parentConversation);
        ArgumentNullException.ThrowIfNull(runtimeState);
        ArgumentNullException.ThrowIfNull(lease);
        using var execution = CancellationTokenSource.CreateLinkedTokenSource(
            hostCancellationToken,
            runtimeState.CancellationTokenSource.Token);
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
        AgentTurnState? turn = null;
        SubagentContinuationTurnCommitter? committer = null;
        var publishPersistedTurn = false;
        var notifyDeadLetter = false;
        try
        {
            var request = CreateRequest(runtimeState, lease);
            turn = new AgentTurnState(lease.ContinuationTurnId, request.Agent);
            committer = new SubagentContinuationTurnCommitter(_deliveryStore, lease, _timeProvider);
            _turnRecorder.BeginTurn(runtimeState, turn);
            await foreach (var streamEvent in _chatRuntime.StreamTurnAsync(request, execution.Token))
            {
                await _turnRecorder.ApplyDetachedEventAsync(
                    runtimeState,
                    turn,
                    streamEvent,
                    committer,
                    execution.Token);
            }

            if (!turn.Completed)
            {
                await _turnRecorder.FinalizeInterruptedAsync(
                    runtimeState,
                    turn,
                    TurnFinalizationKind.Failed,
                    "The continuation stream ended without a terminal event.",
                    committer);
            }

            (publishPersistedTurn, notifyDeadLetter) = ReadDisposition(committer, turn);
        }
        catch (OperationCanceledException) when (Volatile.Read(ref leaseLost) != 0)
        {
            _logger.LogWarning(
                "Subagent continuation lease was lost. ParentConversationId={ParentConversationId} ContinuationTurnId={ContinuationTurnId}",
                lease.ParentConversationId,
                lease.ContinuationTurnId);
        }
        catch (OperationCanceledException)
        {
            if (turn is not null && committer is not null)
            {
                await _turnRecorder.FinalizeInterruptedAsync(
                    runtimeState,
                    turn,
                    TurnFinalizationKind.Failed,
                    "The application stopped the Subagent continuation.",
                    committer);
                (publishPersistedTurn, notifyDeadLetter) = ReadDisposition(committer, turn);
            }
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
            if (turn is not null && committer is not null)
            {
                await _turnRecorder.FinalizeInterruptedAsync(
                    runtimeState,
                    turn,
                    TurnFinalizationKind.Failed,
                    exception.Message,
                    committer);
                (publishPersistedTurn, notifyDeadLetter) = ReadDisposition(committer, turn);
            }
            else
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
            heartbeatCancellation.Cancel();
            await AwaitHeartbeatAsync(heartbeat);
            await _turnEngine.CompleteContinuationAsync(
                runtimeState,
                publishPersistedTurn,
                CancellationToken.None);
        }

        if (notifyDeadLetter)
        {
            _notificationService.ShowSubagentContinuationFailed(
                parentConversation.Id,
                parentConversation.Title,
                "Subagent results could not be continued safely. Open the conversation for details.");
        }
    }

    private DirectChatTurnRequest CreateRequest(
        ConversationRuntimeState runtimeState,
        SubagentDeliveryLease lease)
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
            parent.ModelProfileId,
            parent.ToolPermissionMode,
            _approvalHandler,
            new DirectTurnExecutionContext(
                DirectTurnOrigin.Continuation,
                parent.CapabilityCeiling,
                batch));
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
        SubagentContinuationTurnCommitter committer,
        AgentTurnState turn)
    {
        var deadLetter = committer.Disposition == SubagentContinuationDisposition.DeadLetter;
        var publish = committer.Disposition == SubagentContinuationDisposition.Delivered ||
                      (deadLetter && turn.ToolRunsByCallId.Count > 0);
        return (publish, deadLetter);
    }
}
