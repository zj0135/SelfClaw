using SelfClaw.Core.Models;

namespace SelfClaw.Core.Interfaces;

public interface IConversationTurnRepository
{
    Task<ConversationTurnCommit> StartTurnAsync(ConversationTurnStart start, CancellationToken cancellationToken = default);

    Task<long> ReserveMessageSequenceAsync(Guid conversationId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ConversationTurnRecord>> ListTurnsAsync(Guid conversationId, CancellationToken cancellationToken = default);

    Task CommitProgressAsync(ConversationTurnCommit progress, CancellationToken cancellationToken = default);

    Task<bool> TryFinalizeTurnAsync(ConversationTurnCommit commit, CancellationToken cancellationToken = default);
}
