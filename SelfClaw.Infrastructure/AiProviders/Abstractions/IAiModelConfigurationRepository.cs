using SelfClaw.Infrastructure.AiProviders.Models;

namespace SelfClaw.Infrastructure.AiProviders.Abstractions;

public interface IAiModelConfigurationRepository
{
    Task<IReadOnlyList<AiModelConfiguration>> ListAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(AiModelConfiguration configuration, CancellationToken cancellationToken = default);

    Task DeleteAsync(string model, CancellationToken cancellationToken = default);
}
