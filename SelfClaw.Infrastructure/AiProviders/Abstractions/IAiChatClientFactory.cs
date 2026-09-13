using SelfClaw.Infrastructure.AiProviders.Models;

namespace SelfClaw.Infrastructure.AiProviders.Abstractions;

public interface IAiChatClientFactory
{
    Task<AiProviderClientRequest> PrepareAsync(
        Guid? modelProfileId,
        CancellationToken cancellationToken = default);

    AiChatClientLease Create(AiProviderClientRequest preparation, IReadOnlyList<Microsoft.Extensions.AI.AITool> tools);
}
