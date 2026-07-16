using SelfClaw.Infrastructure.AiProviders.Models.Views;

namespace SelfClaw.Infrastructure.AiProviders.Abstractions;

public interface IAiProviderSettingsService
{
    Task<AiProviderSettingsState> GetStateAsync(CancellationToken cancellationToken = default);

    Task<AiProviderView> SaveProviderAsync(
        SaveProviderCommand command,
        CancellationToken cancellationToken = default);

    Task SetProviderEnabledAsync(
        Guid connectionId,
        bool enabled,
        CancellationToken cancellationToken = default);

    Task DeleteProviderAsync(Guid connectionId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AiModelView>> FetchAndMergeRemoteModelsAsync(
        Guid connectionId,
        CancellationToken cancellationToken = default);

    Task<ConnectivityCheckResult> CheckConnectivityAsync(
        Guid connectionId,
        Guid modelProfileId,
        CancellationToken cancellationToken = default);

    Task<AiModelView> UpsertModelAsync(
        UpsertModelCommand command,
        CancellationToken cancellationToken = default);

    Task SetModelEnabledAsync(
        Guid modelProfileId,
        bool enabled,
        CancellationToken cancellationToken = default);

    Task SetAllModelsEnabledAsync(
        Guid connectionId,
        bool enabled,
        CancellationToken cancellationToken = default);

    Task DeleteModelAsync(Guid modelProfileId, CancellationToken cancellationToken = default);

    Task SetDefaultModelAsync(
        string scope,
        Guid modelProfileId,
        CancellationToken cancellationToken = default);

    /// <summary>Returns the persisted default model profile id for a scope without building the full settings state.</summary>
    Task<Guid?> GetDefaultModelAsync(string scope, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<EnabledModelView>> ListEnabledModelsAsync(
        CancellationToken cancellationToken = default);
}
