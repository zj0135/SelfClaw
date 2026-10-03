using SelfClaw.Core.Models;

namespace SelfClaw.Core.Interfaces;

/// <summary>
/// The durable Queue acceptance and mutation surface. Every state change commits together with
/// the conversation's <c>queue_revision</c> increment.
/// </summary>
public interface IConversationInputStore
{
    Task<ConversationInputAcceptResult> AcceptAsync(ConversationInputAcceptRequest request, CancellationToken cancellationToken = default);

    Task<ConversationInputUpdateResult> EditAsync(ConversationInputEditRequest request, CancellationToken cancellationToken = default);

    Task<ConversationInputUpdateResult> CancelAsync(ConversationInputCancelRequest request, CancellationToken cancellationToken = default);

    Task<ConversationInputUpdateResult> HoldAsync(Guid inputId, int expectedRevision, string reasonCode, string? errorMessage, CancellationToken cancellationToken = default);

    Task<ConversationInputUpdateResult> RetryAsync(Guid inputId, int expectedRevision, CancellationToken cancellationToken = default,
        ConversationInputExecutionSnapshot? executionSnapshot = null);

    Task<ConversationInputRecord?> FindRequestAsync(Guid conversationId, string clientRequestId, CancellationToken cancellationToken = default);

    Task<ConversationInputRecord?> GetInputAsync(Guid inputId, CancellationToken cancellationToken = default);

    Task<ConversationInputQueueState> GetQueueStateAsync(Guid conversationId, CancellationToken cancellationToken = default);

    Task<ConversationInputQueueState> SetPausedAsync(ConversationInputPauseRequest request, CancellationToken cancellationToken = default);
}
