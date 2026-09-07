using SelfClaw.Infrastructure.AiProviders.Abstractions;
using SelfClaw.Infrastructure.AiProviders.Models.Views;
using SelfClaw.Infrastructure.AiProviders.Models;

namespace SelfClaw.Tests.TestDoubles;

internal sealed class StubAiProviderSettingsService : IAiProviderSettingsService
{
    public Task<IReadOnlyList<AiModelConfiguration>> ListModelConfigurationsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<AiModelConfiguration> SaveModelConfigurationAsync(AiModelConfiguration configuration, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task DeleteModelConfigurationAsync(string model, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    internal StubAiProviderSettingsService(Guid? defaultModelProfileId = null)
    {
        DefaultModelProfileId = defaultModelProfileId ?? Guid.NewGuid();
    }

    internal Guid? DefaultModelProfileId { get; set; }

    internal IReadOnlyList<EnabledModelView> EnabledModels { get; set; } = [];

    public Task<Guid?> GetDefaultModelAsync(
        string scope,
        CancellationToken cancellationToken = default)
        => Task.FromResult(DefaultModelProfileId);

    public Task<IReadOnlyList<EnabledModelView>> ListEnabledModelsAsync(
        CancellationToken cancellationToken = default)
        => Task.FromResult(EnabledModels);

    public Task<AiProviderSettingsState> GetStateAsync(CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<AiProviderView> SaveProviderAsync(
        SaveProviderCommand command,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task SetProviderEnabledAsync(
        Guid connectionId,
        bool enabled,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task DeleteProviderAsync(Guid connectionId, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<IReadOnlyList<AiModelView>> FetchAndMergeRemoteModelsAsync(
        Guid connectionId,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<ConnectivityCheckResult> CheckConnectivityAsync(
        Guid connectionId,
        Guid modelProfileId,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<AiModelView> UpsertModelAsync(
        UpsertModelCommand command,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task SetModelEnabledAsync(
        Guid modelProfileId,
        bool enabled,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task SetAllModelsEnabledAsync(
        Guid connectionId,
        bool enabled,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task DeleteModelAsync(Guid modelProfileId, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task SetDefaultModelAsync(
        string scope,
        Guid modelProfileId,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
}
