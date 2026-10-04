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
    private readonly ConversationInputFeatureSwitch _featureSwitch;
    private readonly ConversationInputChangeNotifier _changes;
    private readonly ILogger<ConversationInputService> _logger;

    public ConversationInputService(IConversationInputStore store, IConversationInputSchedulerStore scheduler,
        ConversationInputQueueService queue, ConversationInputDispatcher dispatcher, ConversationRunCoordinator runs,
        IAiModelCatalog models, AgentSettingsService agents, ConversationInputFeatureSwitch featureSwitch,
        ConversationInputChangeNotifier changes, ILogger<ConversationInputService> logger)
    {
        _store = store;
        _scheduler = scheduler;
        _queue = queue;
        _dispatcher = dispatcher;
        _runs = runs;
        _models = models;
        _agents = agents;
        _featureSwitch = featureSwitch;
        _changes = changes;
        _logger = logger;
    }

    public bool QueueEnabled => _queue.QueueEnabled;

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
        // With the queue closed, hold the run slot across acceptance. A check-then-accept would let a
        // second send land while the first turn is still starting, and that extra item would never run.
        ConversationRunHandle? reserved = null;
        if (existing is null && !_featureSwitch.QueueEnabled)
        {
            var state = await _store.GetQueueStateAsync(submission.ConversationId, token).ConfigureAwait(false);
            if (state.Items.Count > 0) return Rejected(ConversationInputReason.PendingInputs);
            reserved = _runs.TryReserve(submission.ConversationId, AgentExecutionMode.Direct, DirectTurnOrigin.Interactive, token);
            if (reserved is null) return Rejected(ConversationInputReason.QueueDisabled);
        }
        var transferred = false;
        try
        {
            var snapshot = existing?.ExecutionSnapshot ?? await CaptureAsync(submission, token).ConfigureAwait(false);
            if (snapshot is null) return Rejected(ConversationInputReason.ModelUnavailable);
            var result = await _store.AcceptAsync(new(submission.ConversationId, submission.ClientRequestId,
                ConversationInputKind.FollowUp, null, submission.Prompt, snapshot, submission.Conversation,
                _featureSwitch.QueueEnabled, Fingerprint(submission)), token).ConfigureAwait(false);
            if (result.Input is not { } input)
                return new(false, null, 0, result.QueueRevision, ConversationInputSubmitOutcome.Rejected, result.Reason);
            _logger.LogInformation("Input {InputId} accepted for {ConversationId}: {Status}.", input.Id, input.ConversationId, result.Status);
            _changes.Notify(submission.ConversationId);
            if (result.Status == ConversationInputAcceptStatus.Accepted)
            {
                transferred = reserved is not null;
                _dispatcher.RequestStart(submission.ConversationId, input.Id, reserved);
            }
            return new(true, input.Id, input.Revision, result.QueueRevision,
                input.Status is ConversationInputStatus.Claimed or ConversationInputStatus.Consumed
                    ? ConversationInputSubmitOutcome.Started : ConversationInputSubmitOutcome.Queued);
        }
        finally
        {
            if (!transferred && reserved is not null) _runs.Complete(reserved, false);
        }
    }

    public async Task StopAsync(Guid conversationId)
    {
        await _scheduler.PauseQueueAsync(conversationId, ConversationInputReason.TurnCancelled, CancellationToken.None).ConfigureAwait(false);
        _changes.Notify(conversationId);
        _runs.Stop(conversationId);
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

    public Task<ConversationInputQueueState> GetStateAsync(Guid conversationId, CancellationToken cancellationToken = default)
        => _queue.GetStateAsync(conversationId, cancellationToken);

    public Task<ConversationInputDetail?> GetDetailAsync(Guid inputId, CancellationToken cancellationToken = default)
        => _queue.GetDetailAsync(inputId, cancellationToken);

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
