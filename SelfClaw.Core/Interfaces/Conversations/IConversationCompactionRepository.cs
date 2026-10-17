using SelfClaw.Core.Models;

namespace SelfClaw.Core.Interfaces;

/// <summary>
/// Read side of compaction records only. Writes happen inside the turn commit transaction, so the
/// summary and the turn's terminal state can never diverge.
/// </summary>
public interface IConversationCompactionRepository
{
    Task InitializeAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ConversationCompactionRecord>> ListAsync(Guid conversationId, CancellationToken cancellationToken = default);

    Task<ConversationCompactionRecord?> GetLatestAsync(Guid conversationId, CancellationToken cancellationToken = default);
}
