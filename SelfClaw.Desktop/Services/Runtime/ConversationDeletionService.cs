using Microsoft.Extensions.Logging;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
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
    private readonly IConversationInputSchedulerStore? _inputs;
    private readonly SubagentActivityService? _activity;
    private readonly IAttachmentStore? _attachments;
    private readonly ILogger<ConversationDeletionService>? _logger;

    public ConversationDeletionService(ConversationRunCoordinator runs, ConversationSessionCoordinator sessions,
        ISubagentConversationLifecycle subagents, ConversationWorkspaceService workspaces, IConversationRepository conversations,
        SubagentActivityService? activity = null, IConversationInputSchedulerStore? inputs = null,
        IAttachmentStore? attachments = null, ILogger<ConversationDeletionService>? logger = null)
    {
        _runs = runs;
        _sessions = sessions;
        _subagents = subagents;
        _workspaces = workspaces;
        _conversations = conversations;
        _activity = activity;
        _inputs = inputs;
        _attachments = attachments;
        _logger = logger;
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
                await DeleteAttachmentsAsync(reservation.ConversationId).ConfigureAwait(false);
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

    private async Task DeleteAttachmentsAsync(Guid conversationId)
    {
        if (_attachments is null) return;
        try
        {
            await _attachments.DeleteConversationAsync(conversationId).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            // The database cascade already succeeded; a leftover directory is reclaimable at next start.
            _logger?.LogWarning(exception, "Failed to delete attachments of conversation {ConversationId}.", conversationId);
        }
    }

    private async Task PauseQueueAsync(Guid conversationId)
    {
        if (_inputs is null) return;
        await _inputs.PauseQueueAsync(conversationId, ConversationInputReason.QueuePaused).ConfigureAwait(false);
    }
}
