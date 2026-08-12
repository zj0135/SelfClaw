using SelfClaw.Core.Models;

namespace SelfClaw.Core.Interfaces;

public interface IGitWorkspaceStore
{
    Task<GitCheckoutRecord?> GetCheckoutAsync(
        Guid workspaceRootId,
        CancellationToken cancellationToken = default);

    Task<GitCheckoutRecord?> GetConversationCheckoutAsync(
        Guid conversationId,
        CancellationToken cancellationToken = default);

    Task SaveAsync(
        GitRepositoryRecord repository,
        GitCheckoutRecord checkout,
        CancellationToken cancellationToken = default);

    Task ReleaseConversationAsync(
        Guid conversationId,
        CancellationToken cancellationToken = default);

    Task DeleteCheckoutAsync(
        Guid workspaceRootId,
        CancellationToken cancellationToken = default);
}
