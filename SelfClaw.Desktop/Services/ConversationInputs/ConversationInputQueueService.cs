using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Desktop.Services.Agents;
using SelfClaw.Desktop.Services.Runtime;

namespace SelfClaw.Desktop.Services.ConversationInputs;

/// <summary>
/// The queue list operations behind the <c>conversation-input/*</c> bridge: edit, cancel, retry,
/// pause and resume. Every mutation is a revision-checked durable update.
/// </summary>
internal sealed class ConversationInputQueueService
{
    private readonly IConversationInputStore _store;
    private readonly IConversationInputUpdateStore _updates;
    private readonly IConversationRepository _conversations;
    private readonly ConversationInputExecutionValidator _validator;
    private readonly ConversationInputDispatcher _dispatcher;
    private readonly ConversationRunCoordinator _runs;
    private readonly AgentSettingsService _agents;
    private readonly ConversationInputFeatureSwitch _featureSwitch;
    private readonly ConversationInputChangeNotifier _changes;

    public ConversationInputQueueService(IConversationInputStore store, IConversationInputUpdateStore updates,
        IConversationRepository conversations, ConversationInputExecutionValidator validator, ConversationInputDispatcher dispatcher,
        ConversationRunCoordinator runs, AgentSettingsService agents, ConversationInputFeatureSwitch featureSwitch,
        ConversationInputChangeNotifier changes)
    {
        _store = store;
        _updates = updates;
        _conversations = conversations;
        _validator = validator;
        _dispatcher = dispatcher;
        _runs = runs;
        _agents = agents;
        _featureSwitch = featureSwitch;
        _changes = changes;
    }

    public bool QueueEnabled => _featureSwitch.QueueEnabled;

    public async Task<ConversationInputUpdateResult> EditAsync(Guid inputId, int expectedRevision, string prompt, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        var result = await _updates.EditAsync(new(inputId, expectedRevision, prompt.Trim()), cancellationToken).ConfigureAwait(false);
        if (result.Input is { } input) _changes.Notify(input.ConversationId);
        return result;
    }

    public async Task<ConversationInputUpdateResult> CancelAsync(Guid inputId, int expectedRevision, CancellationToken cancellationToken = default)
    {
        var result = await _updates.CancelAsync(new(inputId, expectedRevision), cancellationToken).ConfigureAwait(false);
        if (result.Input is { } input) _changes.Notify(input.ConversationId);
        return result;
    }

    public async Task<ConversationInputQueueState> PauseAsync(Guid conversationId, long expectedQueueRevision, string? reason = null,
        CancellationToken cancellationToken = default)
    {
        var state = await _updates.SetPausedAsync(new(conversationId, expectedQueueRevision, true, reason ?? ConversationInputReason.QueuePaused),
            cancellationToken).ConfigureAwait(false);
        if (!state.Paused) throw new ConversationInputRejectedException(ConversationInputReason.QueueRevisionConflict);
        _changes.Notify(conversationId);
        return state;
    }

    public async Task<ConversationInputQueueState> ResumeAsync(Guid conversationId, long expectedQueueRevision, CancellationToken cancellationToken = default)
    {
        await _dispatcher.RecoverOnceAsync(cancellationToken).ConfigureAwait(false);
        if (!_featureSwitch.QueueEnabled) throw new ConversationInputRejectedException(ConversationInputReason.QueueDisabled);
        using var operation = _runs.TryBeginInputOperation(conversationId, cancellationToken)
            ?? throw new ConversationInputRejectedException(ConversationInputReason.ConversationUnavailable);
        var state = await _updates.SetPausedAsync(new(conversationId, expectedQueueRevision, false, null), operation.CancellationToken).ConfigureAwait(false);
        if (state.Paused) throw new ConversationInputRejectedException(ConversationInputReason.QueueRevisionConflict);
        _changes.Notify(conversationId);
        _dispatcher.RequestStart(conversationId);
        return state;
    }

    /// <summary>
    /// Recaptures a Held item against the current definition, model and workspace. A paused queue
    /// still needs an explicit Resume.
    /// </summary>
    public async Task<ConversationInputUpdateResult> RetryAsync(Guid inputId, int expectedRevision, CancellationToken cancellationToken = default)
    {
        var input = await _store.GetInputAsync(inputId, cancellationToken).ConfigureAwait(false);
        if (input is null) return new(ConversationInputUpdateStatus.Missing, null, 0);
        if (!_featureSwitch.QueueEnabled) return Invalid(input, ConversationInputReason.QueueDisabled);
        using var operation = _runs.TryBeginInputOperation(input.ConversationId, cancellationToken)
            ?? throw new ConversationInputRejectedException(ConversationInputReason.ConversationUnavailable);
        var token = operation.CancellationToken;
        if (input.ExecutionSnapshot is not { ModelProfileId: { } modelId } old) return Invalid(input, ConversationInputReason.AgentChanged);
        var conversation = await _conversations.GetConversationAsync(input.ConversationId, token).ConfigureAwait(false)
            ?? throw new ConversationInputRejectedException(ConversationInputReason.ConversationUnavailable);
        var snapshot = ConversationInputSnapshots.Capture(_agents, old.AgentId, modelId, old.WorkspaceRootId, old.WorkspaceRootPath, old.ToolPermissionMode);
        var preparation = await _validator.PrepareAsync(conversation, snapshot, token).ConfigureAwait(false);
        if (preparation.FollowUp is null) return Invalid(input, preparation.Reason);
        var result = await _updates.RetryAsync(inputId, expectedRevision, snapshot, token).ConfigureAwait(false);
        if (result.Status == ConversationInputUpdateStatus.Applied) _changes.Notify(input.ConversationId);
        return result;
    }

    public Task<ConversationInputQueueState> GetStateAsync(Guid conversationId, CancellationToken cancellationToken = default)
        => _store.GetQueueStateAsync(conversationId, cancellationToken);

    public async Task<ConversationInputDetail?> GetDetailAsync(Guid inputId, CancellationToken cancellationToken = default)
    {
        var input = await _store.GetInputAsync(inputId, cancellationToken).ConfigureAwait(false);
        return input is null ? null : new(input.Id, input.Revision, input.Prompt, input.Status);
    }

    private static ConversationInputUpdateResult Invalid(ConversationInputRecord input, string? reason)
        => new(ConversationInputUpdateStatus.Invalid, input, 0, reason);
}
