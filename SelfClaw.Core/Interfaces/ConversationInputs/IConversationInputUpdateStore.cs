using SelfClaw.Core.Models;

namespace SelfClaw.Core.Interfaces;

/// <summary>
/// User-driven conditional updates. Each succeeds only against the expected revision and commits
/// together with the conversation's <c>queue_revision</c> increment.
/// </summary>
public interface IConversationInputUpdateStore
{
    Task<ConversationInputUpdateResult> EditAsync(ConversationInputEditRequest request, CancellationToken cancellationToken = default);

    Task<ConversationInputUpdateResult> CancelAsync(ConversationInputCancelRequest request, CancellationToken cancellationToken = default);

    Task<ConversationInputUpdateResult> RetryAsync(Guid inputId, int expectedRevision, ConversationInputExecutionSnapshot executionSnapshot,
        CancellationToken cancellationToken = default);

    Task<ConversationInputQueueState> SetPausedAsync(ConversationInputPauseRequest request, CancellationToken cancellationToken = default);
}
