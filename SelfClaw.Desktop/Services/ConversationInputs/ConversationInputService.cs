using System.Text.Json;
using Microsoft.Extensions.Logging;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Desktop.Services.Agents;
using SelfClaw.Desktop.Services.Runtime;
using SelfClaw.Infrastructure.Attachments;
using SelfClaw.Infrastructure.Options;

namespace SelfClaw.Desktop.Services.ConversationInputs;

/// <summary>
/// Accepts Direct submissions into the durable queue and wakes the dispatcher; stopping pauses
/// the queue before the active run is cancelled so no queued item can slip in between. Queue list
/// mutations are forwarded to <see cref="ConversationInputQueueService"/>.
/// </summary>
internal sealed class ConversationInputService : IConversationInputCoordinator
{
    private const int MaximumAttachmentsPerInput = 6;

    private readonly IConversationInputStore _store;
    private readonly IConversationInputSchedulerStore _scheduler;
    private readonly ConversationInputQueueService _queue;
    private readonly ConversationInputDispatcher _dispatcher;
    private readonly ConversationRunCoordinator _runs;
    private readonly IAiModelCatalog _models;
    private readonly AgentSettingsService _agents;
    private readonly ConversationInputChangeNotifier _changes;
    private readonly StoragePaths _storagePaths;
    private readonly ILogger<ConversationInputService> _logger;

    public ConversationInputService(IConversationInputStore store, IConversationInputSchedulerStore scheduler,
        ConversationInputQueueService queue, ConversationInputDispatcher dispatcher, ConversationRunCoordinator runs,
        IAiModelCatalog models, AgentSettingsService agents,
        ConversationInputChangeNotifier changes, StoragePaths storagePaths, ILogger<ConversationInputService> logger)
    {
        _store = store;
        _scheduler = scheduler;
        _queue = queue;
        _dispatcher = dispatcher;
        _runs = runs;
        _models = models;
        _agents = agents;
        _changes = changes;
        _storagePaths = storagePaths;
        _logger = logger;
    }

    public async Task<ConversationInputSubmitResult> SubmitAsync(ConversationInputSubmission submission, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(submission);
        if (submission.Agent.Mode != AgentExecutionMode.Direct) return Rejected(ConversationInputReason.DirectOnly);
        var (attachments, rejection) = MapAttachments(submission);
        if (rejection is not null) return Rejected(rejection);
        using var operation = _runs.TryBeginInputOperation(submission.ConversationId, cancellationToken);
        if (operation is null) return Rejected(ConversationInputReason.ConversationUnavailable);
        var token = operation.CancellationToken;
        await _dispatcher.RecoverOnceAsync(token).ConfigureAwait(false);
        var existing = await _store.FindRequestAsync(submission.ConversationId, submission.ClientRequestId, token).ConfigureAwait(false);
        var snapshot = existing?.ExecutionSnapshot ?? await CaptureAsync(submission, token).ConfigureAwait(false);
        if (snapshot is null) return Rejected(ConversationInputReason.ModelUnavailable);
        // The image gate is checked against the model that will actually run, not the sender's claim.
        if (attachments is not null
            && !(snapshot.ModelProfileId is { } modelId
                && await _models.IsModelMultimodalAsync(modelId, token).ConfigureAwait(false)))
        {
            return Rejected(ConversationInputReason.ImageUnsupportedByModel);
        }
        var acceptRequest = new ConversationInputAcceptRequest(submission.ConversationId, submission.ClientRequestId,
            submission.Prompt, snapshot, submission.Conversation, Fingerprint(submission), attachments);
        var result = await _store.AcceptAsync(acceptRequest, token).ConfigureAwait(false);
        if (result.Input is not { } input)
            return new(false, null, 0, result.QueueRevision, ConversationInputSubmitOutcome.Rejected, result.Reason);
        _logger.LogInformation("Input {InputId} accepted for {ConversationId}: {Status}.", input.Id, input.ConversationId, result.Status);
        // A send that starts at once is not backlog: it is claimed and consumed inside the run it
        // launches, so publishing on accept would flash a queue card. The dispatcher publishes instead,
        // once it knows the input started, queued behind a run or was blocked by a paused queue.
        if (result.Status == ConversationInputAcceptStatus.Accepted)
        {
            _dispatcher.RequestStart(submission.ConversationId, input.Id);
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

    private async Task<ConversationInputExecutionSnapshot?> CaptureAsync(ConversationInputSubmission submission, CancellationToken token)
    {
        var modelId = submission.ModelProfileId ?? await _models.GetDefaultModelAsync(AiModelSelectionScopes.DesktopDefault, token).ConfigureAwait(false);
        return modelId is null ? null : ConversationInputSnapshots.Capture(_agents, submission.Agent.Id, modelId.Value,
            submission.WorkspaceRoot?.Id, submission.WorkspaceRoot?.RootPath, submission.ToolPermissionMode);
    }

    private static string Fingerprint(ConversationInputSubmission submission)
        => JsonSerializer.Serialize(new { AgentId = submission.Agent.Id, submission.Agent.Mode, submission.ModelProfileId,
            RootId = submission.WorkspaceRoot?.Id, RootPath = submission.WorkspaceRoot?.RootPath, submission.ToolPermissionMode });

    /// <summary>
    /// Turns stored attachment records into the references the queue persists. The relative path is
    /// derived from the root instead of trusting the sender's path, and a reference outside the root,
    /// off the whitelist or over the size ceiling rejects the whole submission.
    /// </summary>
    private (IReadOnlyList<AttachmentReference>? References, string? Reason) MapAttachments(
        ConversationInputSubmission submission)
    {
        if (submission.Attachments is not { Count: > 0 } attachments) return (null, null);
        if (attachments.Count > MaximumAttachmentsPerInput) return (null, ConversationInputReason.AttachmentLimitExceeded);
        var root = AttachmentPathResolver.ResolveRoot(_storagePaths.AppDataDirectory);
        var references = new List<AttachmentReference>(attachments.Count);
        foreach (var attachment in attachments)
        {
            if (attachment.Kind != MessageAttachmentKind.Image
                || attachment.ByteLength is <= 0 or > AttachmentPathResolver.MaximumByteLength
                || !AttachmentPathResolver.AllowedMediaTypes.Contains(attachment.MediaType)
                || !AttachmentPathResolver.TryCreateRelativePath(root, attachment.StoragePath, out var relativePath)
                || !AttachmentPathResolver.TryResolveExistingFile(root, relativePath, out _))
            {
                return (null, ConversationInputReason.AttachmentInvalid);
            }

            references.Add(new AttachmentReference(attachment.Id, attachment.FileName, attachment.MediaType,
                attachment.ByteLength, relativePath));
        }

        return (references, null);
    }

    private static ConversationInputSubmitResult Rejected(string reason) => new(false, null, 0, 0, ConversationInputSubmitOutcome.Rejected, reason);
}
