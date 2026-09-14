using SelfClaw.Core.Interfaces;
using SelfClaw.Desktop.Services.Subagents;
using SelfClaw.Desktop.Services.Workspace;

namespace SelfClaw.Desktop.Services.Runtime;

internal sealed class ConversationDeletionService
{
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(8);
    private readonly ConversationTurnEngine _engine;
    private readonly ConversationSessionCoordinator _sessions;
    private readonly ISubagentConversationLifecycle _subagents;
    private readonly ConversationWorkspaceService _workspaces;
    private readonly IConversationRepository _conversations;

    public ConversationDeletionService(ConversationTurnEngine engine, ConversationSessionCoordinator sessions,
        ISubagentConversationLifecycle subagents, ConversationWorkspaceService workspaces, IConversationRepository conversations)
    {
        _engine = engine;
        _sessions = sessions;
        _subagents = subagents;
        _workspaces = workspaces;
        _conversations = conversations;
    }

    public async Task DeleteAsync(IReadOnlyCollection<Guid> conversationIds, bool removeWorktree)
    {
        ArgumentNullException.ThrowIfNull(conversationIds);
        foreach (var id in conversationIds) _engine.BeginConversationDeletion(id);
        try
        {
            foreach (var id in conversationIds)
            {
                await _sessions.StopAndRemoveAsync(id, StopTimeout).ConfigureAwait(false);
                await _subagents.CancelAndWaitAsync(id, StopTimeout).ConfigureAwait(false);
            }
            foreach (var id in conversationIds)
            {
                await _workspaces.ReleaseAsync(id, removeWorktree).ConfigureAwait(false);
                await _conversations.DeleteConversationAsync(id).ConfigureAwait(false);
            }
        }
        finally
        {
            foreach (var id in conversationIds) _engine.EndConversationDeletion(id);
        }
    }
}
