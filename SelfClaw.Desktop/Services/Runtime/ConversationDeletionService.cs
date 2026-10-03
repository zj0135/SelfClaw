using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Desktop.Services.Subagents;
using SelfClaw.Desktop.Services.Workspace;

namespace SelfClaw.Desktop.Services.Runtime;

internal sealed class ConversationDeletionService
{
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(8);
    private const int PauseAttempts = 3;
    private readonly ConversationRunCoordinator _runs;
    private readonly ConversationSessionCoordinator _sessions;
    private readonly ISubagentConversationLifecycle _subagents;
    private readonly ConversationWorkspaceService _workspaces;
    private readonly IConversationRepository _conversations;
    private readonly IConversationInputStore? _inputs;
    private readonly SubagentActivityService? _activity;

    public ConversationDeletionService(ConversationRunCoordinator runs, ConversationSessionCoordinator sessions,
        ISubagentConversationLifecycle subagents, ConversationWorkspaceService workspaces, IConversationRepository conversations,
        SubagentActivityService? activity = null, IConversationInputStore? inputs = null)
    {
        _runs = runs;
        _sessions = sessions;
        _subagents = subagents;
        _workspaces = workspaces;
        _conversations = conversations;
        _activity = activity;
        _inputs = inputs;
    }

    public async Task DeleteAsync(IReadOnlyCollection<Guid> conversationIds, bool removeWorktree)
    {
        ArgumentNullException.ThrowIfNull(conversationIds);
        var reservations = new List<ConversationDeletionReservation>();
        var deleted = new HashSet<Guid>();
        try
        {
            foreach (var id in conversationIds.Distinct())
            {
                reservations.Add(_runs.BeginDeletion(id));
                _activity?.SetScopeClosed(id, true);
                await _runs.DrainInputOperationsAsync(id, StopTimeout).ConfigureAwait(false);
                // Closing the durable queue here is what keeps a failed delete paused instead of
                // silently resuming: the tombstone is released, the pause is not.
                await PauseQueueAsync(id).ConfigureAwait(false);
            }
            foreach (var reservation in reservations)
            {
                await _runs.StopAndWaitAsync(reservation.ConversationId, StopTimeout).ConfigureAwait(false);
                await _subagents.CancelAndWaitAsync(reservation.ConversationId, StopTimeout).ConfigureAwait(false);
            }
            foreach (var reservation in reservations)
            {
                await _workspaces.ReleaseAsync(reservation.ConversationId, removeWorktree).ConfigureAwait(false);
                await _conversations.DeleteConversationAsync(reservation.ConversationId).ConfigureAwait(false);
                deleted.Add(reservation.ConversationId);
                _sessions.ForgetTranscript(reservation.ConversationId);
            }
        }
        finally
        {
            foreach (var reservation in reservations)
            {
                var wasDeleted = deleted.Contains(reservation.ConversationId);
                _runs.EndDeletion(reservation, wasDeleted);
                if (!wasDeleted) _activity?.SetScopeClosed(reservation.ConversationId, false);
            }
        }
    }

    private async Task PauseQueueAsync(Guid conversationId)
    {
        if (_inputs is null) return;
        if (_inputs is IConversationInputSchedulerStore scheduler)
        {
            await scheduler.PauseQueueAsync(conversationId, ConversationInputReason.QueuePaused).ConfigureAwait(false);
            return;
        }
        for (var attempt = 0; attempt < PauseAttempts; attempt++)
        {
            var state = await _inputs.GetQueueStateAsync(conversationId).ConfigureAwait(false);
            if (state.Paused) return;
            var paused = await _inputs.SetPausedAsync(new ConversationInputPauseRequest(
                conversationId, state.QueueRevision, true, ConversationInputReason.QueuePaused)).ConfigureAwait(false);
            if (paused.Paused) return;
        }
        throw new InvalidOperationException("The queue could not be paused before deletion.");
    }
}
