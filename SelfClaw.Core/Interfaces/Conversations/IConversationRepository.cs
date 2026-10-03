using SelfClaw.Core.Models;

namespace SelfClaw.Core.Interfaces;

public interface IConversationRepository
{
    Task InitializeAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ConversationRecord>> ListConversationsAsync(CancellationToken cancellationToken = default);

    Task<ConversationRecord?> GetConversationAsync(Guid conversationId, CancellationToken cancellationToken = default);

    Task<ConversationRecord> UpsertConversationAsync(ConversationRecord conversation, CancellationToken cancellationToken = default);

    Task DeleteConversationAsync(Guid conversationId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MessageRecord>> ListMessagesAsync(Guid conversationId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ToolExecutionRecord>> ListToolExecutionsAsync(Guid conversationId, CancellationToken cancellationToken = default);

}
