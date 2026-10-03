using System.Text.Json;
using Microsoft.Extensions.Logging;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Desktop.Services.Agents;
using SelfClaw.Desktop.Services.Runtime;

namespace SelfClaw.Desktop.Services.ConversationInputs;

internal sealed class ConversationInputService : IConversationInputCoordinator
{
    private readonly IConversationInputStore _store;
    private readonly ConversationInputDispatcher _dispatcher;
    private readonly ConversationRunCoordinator _runs;
    private readonly IAiModelCatalog _models;
    private readonly AgentSettingsService _agents;
    private readonly ConversationInputFeatureSwitch _featureSwitch;
    private readonly ConversationInputChangeNotifier _changes;
    private readonly ILogger<ConversationInputService> _logger;

    public ConversationInputService(IConversationInputStore store, ConversationInputDispatcher dispatcher,
        ConversationRunCoordinator runs, IAiModelCatalog models, AgentSettingsService agents,
        ConversationInputFeatureSwitch featureSwitch, ConversationInputChangeNotifier changes,
        ILogger<ConversationInputService> logger)
    {
        _store = store;
        _dispatcher = dispatcher;
        _runs = runs;
        _models = models;
        _agents = agents;
        _featureSwitch = featureSwitch;
        _changes = changes;
        _logger = logger;
    }

    public bool QueueEnabled => _featureSwitch.QueueEnabled;

    public async Task<ConversationInputSubmitResult> SubmitAsync(ConversationInputSubmission submission, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(submission);
        if (submission.Attachments is { Count: > 0 }) return Rejected(ConversationInputReason.AttachmentsUnsupported);
        if (submission.Agent.Mode != AgentExecutionMode.Direct) return Rejected("direct-only");
        using var operation = _runs.TryBeginInputOperation(submission.ConversationId, cancellationToken);
        if (operation is null) return Rejected("conversation-unavailable");
        var token = operation.CancellationToken;
        await _dispatcher.RecoverOnceAsync(token).ConfigureAwait(false);
        var existing = await _store.FindRequestAsync(submission.ConversationId, submission.ClientRequestId, token).ConfigureAwait(false);
        if (existing is null && !_featureSwitch.QueueEnabled && _runs.IsRunning(submission.ConversationId))
            return Rejected(ConversationInputReason.QueueDisabled);
        var snapshot = existing?.ExecutionSnapshot ?? await CaptureAsync(submission, token).ConfigureAwait(false);
        if (snapshot is null) return Rejected("model-unavailable");
        var result = await _store.AcceptAsync(new(submission.ConversationId, submission.ClientRequestId,
            ConversationInputKind.FollowUp, null, submission.Prompt, snapshot,
            submission.Conversation ?? CreateConversation(submission), _featureSwitch.QueueEnabled, Fingerprint(submission)), token).ConfigureAwait(false);
        if (result.Input is not { } input)
            return new(false, null, 0, result.QueueRevision, ConversationInputSubmitOutcome.Rejected, result.Reason);
        _logger.LogInformation("Input {InputId} accepted for {ConversationId}: {Status}.", input.Id, input.ConversationId, result.Status);
        _changes.Notify(submission.ConversationId);
        if (result.Status == ConversationInputAcceptStatus.Accepted)
            _dispatcher.RequestStart(submission.ConversationId, input.Id);
        return new(true, input.Id, input.Revision, result.QueueRevision,
            input.Status is ConversationInputStatus.Claimed or ConversationInputStatus.Consumed
                ? ConversationInputSubmitOutcome.Started : ConversationInputSubmitOutcome.Queued);
    }

    public async Task<ConversationInputUpdateResult> EditAsync(Guid inputId, int expectedRevision, string prompt, CancellationToken cancellationToken = default)
    {
        var result = await _store.EditAsync(new(inputId, expectedRevision, prompt.Trim()), cancellationToken).ConfigureAwait(false);
        if (result.Input is { } input) _changes.Notify(input.ConversationId);
        return result;
    }

    public async Task<ConversationInputUpdateResult> CancelAsync(Guid inputId, int expectedRevision, CancellationToken cancellationToken = default)
    {
        var result = await _store.CancelAsync(new(inputId, expectedRevision), cancellationToken).ConfigureAwait(false);
        if (result.Input is { } input) _changes.Notify(input.ConversationId);
        return result;
    }

    public async Task StopAsync(Guid conversationId)
    {
        await _dispatcher.PauseQueueAsync(conversationId, ConversationInputReason.TurnCancelled).ConfigureAwait(false);
        _runs.Stop(conversationId);
    }

    public async Task<ConversationInputQueueState> PauseAsync(Guid conversationId, long expectedQueueRevision, string? reason, CancellationToken cancellationToken = default)
    {
        var state = await _store.SetPausedAsync(new(conversationId, expectedQueueRevision, true, reason), cancellationToken).ConfigureAwait(false);
        if (!state.Paused) throw new InvalidOperationException("queue-revision-conflict");
        _changes.Notify(conversationId);
        return state;
    }

    public async Task<ConversationInputQueueState> ResumeAsync(Guid conversationId, long expectedQueueRevision, CancellationToken cancellationToken = default)
    {
        await _dispatcher.RecoverOnceAsync(cancellationToken).ConfigureAwait(false);
        if (!_featureSwitch.QueueEnabled) return await GetStateAsync(conversationId, cancellationToken).ConfigureAwait(false);
        using var operation = _runs.TryBeginInputOperation(conversationId, cancellationToken);
        if (operation is null) throw new InvalidOperationException("conversation-unavailable");
        var state = await _store.SetPausedAsync(new(conversationId, expectedQueueRevision, false, null), operation.CancellationToken).ConfigureAwait(false);
        if (state.Paused) throw new InvalidOperationException("queue-revision-conflict");
        _changes.Notify(conversationId);
        _dispatcher.RequestStart(conversationId);
        return state;
    }

    public async Task<ConversationInputUpdateResult> RetryAsync(Guid inputId, int expectedRevision, CancellationToken cancellationToken = default)
    {
        var input = await _store.GetInputAsync(inputId, cancellationToken).ConfigureAwait(false);
        if (input is null) return new(ConversationInputUpdateStatus.Missing, null, 0);
        if (!_featureSwitch.QueueEnabled) return new(ConversationInputUpdateStatus.Invalid, input, 0);
        using var operation = _runs.TryBeginInputOperation(input.ConversationId, cancellationToken);
        if (operation is null) throw new InvalidOperationException("conversation-unavailable");
        if (input.ExecutionSnapshot is not { ModelProfileId: { } modelId } old)
            return new(ConversationInputUpdateStatus.Invalid, input, 0);
        var snapshot = ConversationInputSnapshots.Capture(_agents, old.AgentId, modelId, old.WorkspaceRootId, old.WorkspaceRootPath, old.ToolPermissionMode);
        await _dispatcher.ValidateRetryAsync(input.ConversationId, snapshot, operation.CancellationToken).ConfigureAwait(false);
        var result = await _store.RetryAsync(inputId, expectedRevision, operation.CancellationToken, snapshot).ConfigureAwait(false);
        if (result.Status == ConversationInputUpdateStatus.Applied) _changes.Notify(input.ConversationId);
        // Retrying a held item recaptures it; an explicitly paused queue still requires Resume.
        return result;
    }

    public Task<ConversationInputQueueState> GetStateAsync(Guid conversationId, CancellationToken cancellationToken = default)
        => _store.GetQueueStateAsync(conversationId, cancellationToken);

    public async Task<ConversationInputDetail?> GetDetailAsync(Guid inputId, CancellationToken cancellationToken = default)
    {
        var input = await _store.GetInputAsync(inputId, cancellationToken).ConfigureAwait(false);
        return input is null ? null : new(input.Id, input.Revision, input.Prompt, input.Status);
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

    private static ConversationRecord CreateConversation(ConversationInputSubmission submission)
    {
        var now = DateTimeOffset.UtcNow;
        var title = submission.Prompt.ReplaceLineEndings(" ").Trim();
        return new(submission.ConversationId, title.Length > 48 ? title[..48] + "..." : title,
            submission.WorkspaceRoot?.Id, ConversationMode.Programming, submission.ToolPermissionMode, submission.Agent.Id, now, now);
    }
}
