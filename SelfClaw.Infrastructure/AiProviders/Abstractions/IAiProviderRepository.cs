using SelfClaw.Infrastructure.AiProviders.Models;
namespace SelfClaw.Infrastructure.AiProviders.Abstractions;

public interface IAiProviderRepository
{
    Task InitializeAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AiProviderConnection>> ListProviderConnectionsAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AiProviderConnection>> ListAllProviderConnectionsAsync(CancellationToken cancellationToken = default);

    Task<AiProviderConnection?> GetProviderConnectionAsync(Guid providerConnectionId, CancellationToken cancellationToken = default);

    Task<AiProviderConnection> UpsertProviderConnectionAsync(AiProviderConnection connection, CancellationToken cancellationToken = default);

    Task SetProviderConnectionEnabledAsync(Guid providerConnectionId, bool isEnabled, CancellationToken cancellationToken = default);

    Task DeleteProviderConnectionAsync(Guid providerConnectionId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AiModelProfile>> ListModelProfilesAsync(Guid? providerConnectionId = null, CancellationToken cancellationToken = default);

    Task<AiModelProfile?> GetModelProfileAsync(Guid modelProfileId, CancellationToken cancellationToken = default);

    Task<AiModelProfile> UpsertModelProfileAsync(AiModelProfile profile, CancellationToken cancellationToken = default);

    Task SetModelProfileEnabledAsync(Guid modelProfileId, bool isEnabled, CancellationToken cancellationToken = default);

    Task SetAllModelProfilesEnabledAsync(Guid providerConnectionId, bool isEnabled, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AiModelProfile>> ListEnabledModelProfilesAsync(CancellationToken cancellationToken = default);

    Task DeleteModelProfileAsync(Guid modelProfileId, CancellationToken cancellationToken = default);

    Task<AiModelProfileSelection?> GetModelProfileSelectionAsync(string scope, CancellationToken cancellationToken = default);

    Task<AiModelProfileSelection> SetModelProfileSelectionAsync(AiModelProfileSelection selection, CancellationToken cancellationToken = default);
}
