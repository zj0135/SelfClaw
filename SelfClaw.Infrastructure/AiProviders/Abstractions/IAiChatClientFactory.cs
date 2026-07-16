using SelfClaw.Infrastructure.AiProviders.Models;

namespace SelfClaw.Infrastructure.AiProviders.Abstractions;

public interface IAiChatClientFactory
{
    Task<AiChatClientLease> CreateAsync(
        Guid modelProfileId,
        AiChatRuntimeInputs inputs,
        CancellationToken cancellationToken = default);

    Task<AiChatClientLease> CreateForScopeAsync(
        string scope,
        AiChatRuntimeInputs inputs,
        CancellationToken cancellationToken = default);
}
