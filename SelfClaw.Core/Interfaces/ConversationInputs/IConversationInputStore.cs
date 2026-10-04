using SelfClaw.Core.Models;

namespace SelfClaw.Core.Interfaces;

/// <summary>
/// The durable Queue acceptance and read surface. Acceptance commits together with the
/// conversation's <c>queue_revision</c> increment.
/// </summary>
public interface IConversationInputStore
{
    Task<ConversationInputAcceptResult> AcceptAsync(ConversationInputAcceptRequest request, CancellationToken cancellationToken = default);

    Task<ConversationInputRecord?> FindRequestAsync(Guid conversationId, string clientRequestId, CancellationToken cancellationToken = default);

    Task<ConversationInputRecord?> GetInputAsync(Guid inputId, CancellationToken cancellationToken = default);

    Task<ConversationInputQueueState> GetQueueStateAsync(Guid conversationId, CancellationToken cancellationToken = default);
}
