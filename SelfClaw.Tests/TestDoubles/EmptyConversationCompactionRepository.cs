using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;

namespace SelfClaw.Tests.TestDoubles;

internal sealed class EmptyConversationCompactionRepository : IConversationCompactionRepository
{
    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<IReadOnlyList<ConversationCompactionRecord>> ListAsync(
        Guid conversationId, CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<ConversationCompactionRecord>>([]);

    public Task<ConversationCompactionRecord?> GetLatestAsync(
        Guid conversationId, CancellationToken cancellationToken = default)
        => Task.FromResult<ConversationCompactionRecord?>(null);
}
