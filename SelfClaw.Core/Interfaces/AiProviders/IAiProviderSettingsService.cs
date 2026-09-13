using SelfClaw.Core.Models;

namespace SelfClaw.Core.Interfaces;

public interface IAiProviderSettingsService
{
    Task<IReadOnlyList<AiModelConfiguration>> ListModelConfigurationsAsync(CancellationToken cancellationToken = default);

    Task<AiModelConfiguration> SaveModelConfigurationAsync(AiModelConfiguration configuration, CancellationToken cancellationToken = default);

    Task DeleteModelConfigurationAsync(string model, CancellationToken cancellationToken = default);

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

}
