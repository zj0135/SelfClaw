using System.Text.Json;
using Microsoft.Extensions.Logging;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Desktop.Services.Agents;
using SelfClaw.Desktop.Services.Runtime;

namespace SelfClaw.Desktop.Services.ConversationInputs;

/// <summary>
/// Accepts Direct submissions into the durable queue and wakes the dispatcher; stopping pauses
/// the queue before the active run is cancelled so no queued item can slip in between. Queue list
/// mutations are forwarded to <see cref="ConversationInputQueueService"/>.
/// </summary>
internal sealed class ConversationInputService : IConversationInputCoordinator
{
    private readonly IConversationInputStore _store;
    private readonly IConversationInputSchedulerStore _scheduler;
    private readonly ConversationInputQueueService _queue;
    private readonly ConversationInputDispatcher _dispatcher;
    private readonly ConversationRunCoordinator _runs;
    private readonly IAiModelCatalog _models;
    private readonly AgentSettingsService _agents;
    private readonly ConversationInputChangeNotifier _changes;
    private readonly ILogger<ConversationInputService> _logger;

    public ConversationInputService(IConversationInputStore store, IConversationInputSchedulerStore scheduler,
        ConversationInputQueueService queue, ConversationInputDispatcher dispatcher, ConversationRunCoordinator runs,
        IAiModelCatalog models, AgentSettingsService agents,
        ConversationInputChangeNotifier changes, ILogger<ConversationInputService> logger)
    {
        _store = store;
        _scheduler = scheduler;
        _queue = queue;
        _dispatcher = dispatcher;
        _runs = runs;
        _models = models;
        _agents = agents;
        _changes = changes;
        _logger = logger;
    }

    public async Task<ConversationInputSubmitResult> SubmitAsync(ConversationInputSubmission submission, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(submission);
        if (submission.Attachments is { Count: > 0 }) return Rejected(ConversationInputReason.AttachmentsUnsupported);
        if (submission.Agent.Mode != AgentExecutionMode.Direct) return Rejected(ConversationInputReason.DirectOnly);
        using var operation = _runs.TryBeginInputOperation(submission.ConversationId, cancellationToken);
        if (operation is null) return Rejected(ConversationInputReason.ConversationUnavailable);
        var token = operation.CancellationToken;
        await _dispatcher.RecoverOnceAsync(token).ConfigureAwait(false);
        var existing = await _store.FindRequestAsync(submission.ConversationId, submission.ClientRequestId, token).ConfigureAwait(false);
        var requestsSteer = submission.Kind == ConversationInputKind.Steer;
        var (kind, targetTurnId, fallbackReason, session) = existing is not null
            ? (existing.Kind, existing.TargetTurnId, (string?)null, (DirectTurnInputSession?)null)
            : ResolveSteerIntent(submission, requestsSteer);
        var snapshot = existing?.ExecutionSnapshot ?? await CaptureAsync(submission, token).ConfigureAwait(false);
        if (snapshot is null) return Rejected(ConversationInputReason.ModelUnavailable);
        var acceptRequest = new ConversationInputAcceptRequest(submission.ConversationId, submission.ClientRequestId,
            kind, targetTurnId, submission.Prompt, snapshot, submission.Conversation,
            Fingerprint(submission), fallbackReason);
        ConversationInputAcceptResult result;
        if (kind == ConversationInputKind.Steer && session is not null)
        {
            // Accept under the session gate: either the steer commits before the final close and the
            // loop claims it, or the closed intake turns it into a follow-up here.
            result = await session.TryAcceptSteerAsync(
                    acceptToken => _store.AcceptAsync(acceptRequest, acceptToken), token).ConfigureAwait(false)
                ?? await _store.AcceptAsync(acceptRequest with
                {
                    Kind = ConversationInputKind.FollowUp,
                    TargetTurnId = null,
                    ReasonCode = fallbackReason ?? ConversationInputReason.TargetTurnClosed
                }, token).ConfigureAwait(false);
        }
        else
        {
            result = await _store.AcceptAsync(acceptRequest, token).ConfigureAwait(false);
        }
        if (result.Input is not { } input)
            return new(false, null, 0, result.QueueRevision, ConversationInputSubmitOutcome.Rejected, result.Reason);
        _logger.LogInformation("Input {InputId} accepted for {ConversationId}: {Status}.", input.Id, input.ConversationId, result.Status);
        _changes.Notify(submission.ConversationId);
        if (result.Status == ConversationInputAcceptStatus.Accepted)
        {
            if (input.Kind == ConversationInputKind.Steer) session?.NotifySteerAccepted();
            else _dispatcher.RequestStart(submission.ConversationId, input.Id);
        }

        return new(true, input.Id, input.Revision, result.QueueRevision,
            input.Status is ConversationInputStatus.Claimed or ConversationInputStatus.Consumed
                ? ConversationInputSubmitOutcome.Started : ConversationInputSubmitOutcome.Queued,
            result.Reason);
    }

    public async Task StopAsync(Guid conversationId)
    {
        // Stop closes steer intake and settles unconsumed steers before the run token is cancelled:
        // an accepted steer can no longer be consumed after this point. A user stop also drops the
        // backlog so nothing queued before the cancelled turn runs after it and the next send is not
        // stranded behind a manual resume. With no live turn there is nothing to stop: Esc must not
        // pause or mutate an idle queue.
        if (_runs.GetActiveRun(conversationId) is not { } handle) return;
        try
        {
            if (handle.InputSession is { } session)
            {
                await session.HoldUnconsumedSteersAsync(ConversationInputReason.TurnCancelled).ConfigureAwait(false);
            }

            await _scheduler.DiscardQueuedAsync(conversationId, ConversationInputReason.TurnCancelled, CancellationToken.None).ConfigureAwait(false);
            _changes.Notify(conversationId);
            // Only a durably cleared queue may skip the dispatcher's failure pause; if the discard threw,
            // the run still ends below but the surviving backlog keeps the pause-on-cancel protection.
            handle.MarkUserInitiatedStop();
        }
        finally
        {
            _runs.Stop(conversationId);
        }
    }

    public Task<ConversationInputUpdateResult> EditAsync(Guid inputId, int expectedRevision, string prompt, CancellationToken cancellationToken = default)
        => _queue.EditAsync(inputId, expectedRevision, prompt, cancellationToken);

    public Task<ConversationInputUpdateResult> CancelAsync(Guid inputId, int expectedRevision, CancellationToken cancellationToken = default)
        => _queue.CancelAsync(inputId, expectedRevision, cancellationToken);

    public Task<ConversationInputQueueState> PauseAsync(Guid conversationId, long expectedQueueRevision, string? reason, CancellationToken cancellationToken = default)
        => _queue.PauseAsync(conversationId, expectedQueueRevision, reason, cancellationToken);

    public Task<ConversationInputQueueState> ResumeAsync(Guid conversationId, long expectedQueueRevision, CancellationToken cancellationToken = default)
        => _queue.ResumeAsync(conversationId, expectedQueueRevision, cancellationToken);

    public Task<ConversationInputUpdateResult> RetryAsync(Guid inputId, int expectedRevision, CancellationToken cancellationToken = default)
        => _queue.RetryAsync(inputId, expectedRevision, cancellationToken);

    public Task<ConversationInputUpdateResult> PromoteAsync(Guid inputId, int expectedRevision, Guid targetTurnId,
        CancellationToken cancellationToken = default)
        => _queue.PromoteAsync(inputId, expectedRevision, targetTurnId, cancellationToken);

    public Task<ConversationInputQueueState> GetStateAsync(Guid conversationId, CancellationToken cancellationToken = default)
        => _queue.GetStateAsync(conversationId, cancellationToken);

    public Task<ConversationInputDetail?> GetDetailAsync(Guid inputId, CancellationToken cancellationToken = default)
        => _queue.GetDetailAsync(inputId, cancellationToken);

    private (ConversationInputKind Kind, Guid? TargetTurnId, string? FallbackReason, DirectTurnInputSession? Session)
        ResolveSteerIntent(ConversationInputSubmission submission, bool requestsSteer)
    {
        if (!requestsSteer) return (ConversationInputKind.FollowUp, null, null, null);
        var handle = _runs.GetActiveRun(submission.ConversationId);
        var (canSteer, reason, targetTurnId) = ConversationInputSteerPolicy.Evaluate(handle);
        if (!canSteer || targetTurnId != submission.TargetTurnId)
        {
            return (ConversationInputKind.FollowUp, null, reason ?? ConversationInputReason.TargetTurnClosed, null);
        }

        return (ConversationInputKind.Steer, targetTurnId, null, handle!.InputSession);
    }

    private async Task<ConversationInputExecutionSnapshot?> CaptureAsync(ConversationInputSubmission submission, CancellationToken token)
    {
        var modelId = submission.ModelProfileId ?? await _models.GetDefaultModelAsync(AiModelSelectionScopes.DesktopDefault, token).ConfigureAwait(false);
        return modelId is null ? null : ConversationInputSnapshots.Capture(_agents, submission.Agent.Id, modelId.Value,
            submission.WorkspaceRoot?.Id, submission.WorkspaceRoot?.RootPath, submission.ToolPermissionMode);
    }

    private static string Fingerprint(ConversationInputSubmission submission)
        => JsonSerializer.Serialize(new { AgentId = submission.Agent.Id, submission.Agent.Mode, submission.ModelProfileId,
            RootId = submission.WorkspaceRoot?.Id, RootPath = submission.WorkspaceRoot?.RootPath, submission.ToolPermissionMode });

    private static ConversationInputSubmitResult Rejected(string reason) => new(false, null, 0, 0, ConversationInputSubmitOutcome.Rejected, reason);
}
