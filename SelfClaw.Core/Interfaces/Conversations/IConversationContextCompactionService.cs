using SelfClaw.Core.Models;

namespace SelfClaw.Core.Interfaces;

public interface IConversationContextCompactionService
{
    Task<IReadOnlyList<MessageRecord>> PrepareMessagesAsync(
        Guid conversationId,
        ProviderProfile profile,
        string apiKey,
        IReadOnlyList<MessageRecord> messages,
        int modelContextWindow,
        int modelAutoCompactTokenLimit,
        CancellationToken cancellationToken = default);
}
