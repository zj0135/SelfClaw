using SelfClaw.Core.Interfaces;
using SelfClaw.Desktop.Services.Subagents;
using SelfClaw.Desktop.Services.Workspace;

namespace SelfClaw.Desktop.Services.Runtime;

internal sealed class ConversationDeletionService
{
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(8);
    private readonly ConversationRunCoordinator _runs;
    private readonly ConversationSessionCoordinator _sessions;
    private readonly ISubagentConversationLifecycle _subagents;
    private readonly ConversationWorkspaceService _workspaces;
    private readonly IConversationRepository _conversations;
    private readonly SubagentActivityService? _activity;

    public ConversationDeletionService(ConversationRunCoordinator runs, ConversationSessionCoordinator sessions,
        ISubagentConversationLifecycle subagents, ConversationWorkspaceService workspaces, IConversationRepository conversations,
        SubagentActivityService? activity = null)
    {
        _runs = runs;
        _sessions = sessions;
        _subagents = subagents;
        _workspaces = workspaces;
        _conversations = conversations;
        _activity = activity;
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
}
