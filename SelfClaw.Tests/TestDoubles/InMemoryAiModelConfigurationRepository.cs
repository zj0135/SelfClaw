using SelfClaw.Infrastructure.AiProviders.Abstractions;
using SelfClaw.Infrastructure.AiProviders.Models;

namespace SelfClaw.Tests.TestDoubles;

internal sealed class InMemoryAiModelConfigurationRepository : IAiModelConfigurationRepository
{
    private readonly Dictionary<string, AiModelConfiguration> _configurations = new(StringComparer.Ordinal);

    public Task<IReadOnlyList<AiModelConfiguration>> ListAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<AiModelConfiguration>>(_configurations.Values.ToArray());

    public Task SaveAsync(AiModelConfiguration configuration, CancellationToken cancellationToken = default)
    {
        _configurations[configuration.Model] = configuration;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string model, CancellationToken cancellationToken = default)
    {
        _configurations.Remove(model);
        return Task.CompletedTask;
    }
}
