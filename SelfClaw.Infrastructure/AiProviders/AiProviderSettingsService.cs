using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.AI;
using SelfClaw.Core.Interfaces;
using SelfClaw.Infrastructure.AiProviders.Abstractions;
using SelfClaw.Infrastructure.AiProviders.Catalog;
using SelfClaw.Infrastructure.AiProviders.Http;
using SelfClaw.Infrastructure.AiProviders.Models;
using SelfClaw.Infrastructure.AiProviders.Models.Views;

namespace SelfClaw.Infrastructure.AiProviders;

internal sealed class AiProviderSettingsService : IAiProviderSettingsService
{
    internal const string ApiKeySecretName = AiProviderSecrets.ApiKeySecretName;

    private const string DisplayNameKey = "display.name";
    private const string ContextLengthKey = "display.contextLength";
    private const string MaxOutputTokensKey = "display.maxOutputTokens";
    private const string PriceInKey = "display.priceInPerMTok";
    private const string PriceOutKey = "display.priceOutPerMTok";
    private const string PriceCacheWriteKey = "display.priceCacheWritePerMTok";
    private const string PriceCacheReadKey = "display.priceCacheReadPerMTok";

    private readonly IAiProviderRepository _repository;
    private readonly IAiProviderRegistry _registry;
    private readonly ISecretProtector _secretProtector;
    private readonly AiProviderHttpClientProvider _httpClientProvider;

    public AiProviderSettingsService(
        IAiProviderRepository repository,
        IAiProviderRegistry registry,
        ISecretProtector secretProtector,
        AiProviderHttpClientProvider? httpClientProvider = null)
    {
        _repository = repository;
        _registry = registry;
        _secretProtector = secretProtector;
        _httpClientProvider = httpClientProvider ?? new AiProviderHttpClientProvider();
    }

    public async Task<AiProviderSettingsState> GetStateAsync(CancellationToken cancellationToken = default)
    {
        var connections = await _repository.ListAllProviderConnectionsAsync(cancellationToken);
        var profiles = await _repository.ListModelProfilesAsync(cancellationToken: cancellationToken);
        var profilesByConnection = profiles
            .GroupBy(profile => profile.ProviderConnectionId)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<AiModelProfile>)group.ToArray());
        var knownCatalogIds = AiProviderCatalog.Entries
            .Select(entry => entry.CatalogId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var views = new List<AiProviderView>();

        foreach (var catalogEntry in AiProviderCatalog.Entries)
        {
            var matches = connections
                .Where(connection => string.Equals(
                    connection.CatalogId,
                    catalogEntry.CatalogId,
                    StringComparison.OrdinalIgnoreCase))
                .ToArray();

            if (matches.Length == 0)
            {
                views.Add(CreatePlaceholderView(catalogEntry));
                continue;
            }

            foreach (var connection in matches)
            {
                profilesByConnection.TryGetValue(connection.Id, out var connectionProfiles);
                views.Add(await CreateProviderViewAsync(
                    connection,
                    catalogEntry,
                    connectionProfiles ?? [],
                    cancellationToken));
            }
        }

        foreach (var connection in connections.Where(connection => !knownCatalogIds.Contains(connection.CatalogId)))
        {
            profilesByConnection.TryGetValue(connection.Id, out var connectionProfiles);
            views.Add(await CreateProviderViewAsync(
                connection,
                AiProviderCatalog.GetRequired(connection.CatalogId),
                connectionProfiles ?? [],
                cancellationToken));
        }

        var defaultSelection = await _repository.GetModelProfileSelectionAsync(
            AiModelSelectionScopes.DesktopDefault,
            cancellationToken);
        return new AiProviderSettingsState(
            views,
            defaultSelection?.ModelProfileId,
            AiProviderProtocols.CustomProtocolOptions);
    }

    public async Task<AiProviderView> SaveProviderAsync(
        SaveProviderCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command.Name);
        if (!command.Endpoint.IsAbsoluteUri)
        {
            throw new ArgumentException("Provider endpoint must be an absolute URI.", nameof(command));
        }

        var catalogEntry = AiProviderCatalog.GetRequired(command.CatalogId);
        AiProviderConnection? existing = null;
        if (command.Id.HasValue)
        {
            existing = await _repository.GetProviderConnectionAsync(command.Id.Value, cancellationToken)
                ?? throw new KeyNotFoundException($"AI provider connection '{command.Id.Value}' was not found.");
        }

        // The protocol (provider kind) and catalog membership are fixed once a connection
        // exists: preserve them on update, take the command's choice on create, and fall back
        // to the catalog default. This keeps an api-key or base-url edit (which carries no
        // provider kind) from resetting a custom connection's protocol.
        var providerKind = existing?.ProviderKind ?? command.ProviderKind ?? catalogEntry.ProviderKind;
        var catalogId = existing?.CatalogId ?? catalogEntry.CatalogId;
        var authKind = AiProviderProtocols.ResolveAuthKind(providerKind);

        var credentialRefs = existing?.CredentialRefs.ToDictionary(pair => pair.Key, pair => pair.Value)
            ?? new Dictionary<string, string>(StringComparer.Ordinal);
        credentialRefs.TryGetValue(ApiKeySecretName, out var existingApiKeyRef);

        if (authKind == AiProviderAuthKind.None)
        {
            if (!string.IsNullOrWhiteSpace(existingApiKeyRef))
            {
                await _secretProtector.DeleteSecretAsync(existingApiKeyRef, cancellationToken);
            }

            credentialRefs.Remove(ApiKeySecretName);
        }
        else if (command.ApiKey is not null)
        {
            if (string.IsNullOrWhiteSpace(command.ApiKey))
            {
                if (!string.IsNullOrWhiteSpace(existingApiKeyRef))
                {
                    await _secretProtector.DeleteSecretAsync(existingApiKeyRef, cancellationToken);
                }

                credentialRefs.Remove(ApiKeySecretName);
            }
            else
            {
                credentialRefs[ApiKeySecretName] = await _secretProtector.StoreSecretAsync(
                    command.ApiKey,
                    existingApiKeyRef,
                    cancellationToken);
            }
        }

        // On update the catalog membership can differ from the command; resolve the entry that
        // actually backs the persisted connection so the returned view carries matching metadata.
        var effectiveCatalog = string.Equals(catalogId, catalogEntry.CatalogId, StringComparison.OrdinalIgnoreCase)
            ? catalogEntry
            : AiProviderCatalog.GetRequired(catalogId);
        var connectionOptions = ResolveConnectionOptions(command, existing, providerKind, effectiveCatalog);

        var now = DateTimeOffset.UtcNow;
        var connection = new AiProviderConnection(
            existing?.Id ?? command.Id ?? Guid.NewGuid(),
            catalogId,
            command.Name.Trim(),
            providerKind,
            command.Endpoint,
            authKind,
            credentialRefs,
            connectionOptions,
            existing?.CreatedAtUtc ?? now,
            now,
            existing?.IsEnabled ?? command.Enabled ?? true);
        await _repository.UpsertProviderConnectionAsync(connection, cancellationToken);

        var profiles = await _repository.ListModelProfilesAsync(connection.Id, cancellationToken);
        return await CreateProviderViewAsync(connection, effectiveCatalog, profiles, cancellationToken);
    }

    public Task SetProviderEnabledAsync(
        Guid connectionId,
        bool enabled,
        CancellationToken cancellationToken = default)
        => _repository.SetProviderConnectionEnabledAsync(connectionId, enabled, cancellationToken);

    public async Task DeleteProviderAsync(Guid connectionId, CancellationToken cancellationToken = default)
    {
        var connection = await GetRequiredConnectionAsync(connectionId, cancellationToken);
        foreach (var secretRef in connection.CredentialRefs.Values.Distinct(StringComparer.Ordinal))
        {
            await _secretProtector.DeleteSecretAsync(secretRef, cancellationToken);
        }

        await _repository.DeleteProviderConnectionAsync(connectionId, cancellationToken);
    }

    public async Task<IReadOnlyList<AiModelView>> FetchAndMergeRemoteModelsAsync(
        Guid connectionId,
        CancellationToken cancellationToken = default)
    {
        var connection = await GetRequiredConnectionAsync(connectionId, cancellationToken);
        var catalogEntry = AiProviderCatalog.GetRequired(connection.CatalogId);
        var adapter = _registry.GetRequiredAdapter(connection.ProviderKind);
        if (!catalogEntry.SupportsModelListing || !adapter.SupportsModelListing)
        {
            throw new NotSupportedException(
                $"AI provider connection '{connection.Name}' does not support remote model listing.");
        }

        var secrets = await ResolveSecretsAsync(connection, cancellationToken);
        var remoteModels = await adapter.ListModelsAsync(connection, secrets, cancellationToken);
        var defaultApiFormat = ResolveDefaultApiFormat(connection, catalogEntry, adapter);
        var existingModels = await _repository.ListModelProfilesAsync(connectionId, cancellationToken);
        var modelsById = existingModels
            .GroupBy(profile => profile.Model, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var visitedRemoteIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var descriptor in remoteModels)
        {
            if (string.IsNullOrWhiteSpace(descriptor.ModelId) || !visitedRemoteIds.Add(descriptor.ModelId))
            {
                continue;
            }

            var now = DateTimeOffset.UtcNow;
            if (!modelsById.TryGetValue(descriptor.ModelId, out var existing))
            {
                var profile = new AiModelProfile(
                    Guid.NewGuid(),
                    connectionId,
                    descriptor.DisplayName ?? descriptor.ModelId,
                    defaultApiFormat,
                    descriptor.ModelId,
                    new AiSamplingOptions(false, 0.7, false, 0.7),
                    MergeDisplayMetadata(EmptyJsonDictionary(), descriptor, out _),
                    now,
                    now,
                    IsEnabled: false);
                await _repository.UpsertModelProfileAsync(profile, cancellationToken);
                modelsById.Add(profile.Model, profile);
                continue;
            }

            var mergedOptions = MergeDisplayMetadata(existing.ModelOptions, descriptor, out var changed);
            if (changed)
            {
                var updated = existing with { ModelOptions = mergedOptions, UpdatedAtUtc = now };
                await _repository.UpsertModelProfileAsync(updated, cancellationToken);
                modelsById[updated.Model] = updated;
            }
        }

        var mergedModels = await _repository.ListModelProfilesAsync(connectionId, cancellationToken);
        return mergedModels.Select(CreateModelView).ToArray();
    }

    public async Task<ConnectivityCheckResult> CheckConnectivityAsync(
        Guid connectionId,
        Guid modelProfileId,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var connection = await GetRequiredConnectionAsync(connectionId, cancellationToken);
            var profile = await GetRequiredModelAsync(modelProfileId, cancellationToken);
            if (profile.ProviderConnectionId != connectionId)
            {
                throw new InvalidOperationException(
                    $"AI model profile '{modelProfileId}' does not belong to provider connection '{connectionId}'.");
            }

            var adapter = _registry.GetRequiredAdapter(connection.ProviderKind);
            if (!adapter.SupportsApiFormat(profile.ApiFormat))
            {
                throw new NotSupportedException(
                    $"Provider '{connection.Name}' does not support API format '{profile.ApiFormat}'.");
            }

            var secrets = await ResolveSecretsAsync(connection, cancellationToken);
            var request = new AiProviderClientRequest(connection, profile, secrets, false, []);
            using var client = adapter.CreateChatClient(request);
            var options = adapter.CreateChatOptions(request);
            options.MaxOutputTokens = 1;
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(_httpClientProvider.GetNonStreamingTimeout(connection));
            await client.GetResponseAsync(
                [new ChatMessage(ChatRole.User, "ping")],
                options,
                timeoutSource.Token);

            stopwatch.Stop();
            return new ConnectivityCheckResult(true, stopwatch.ElapsedMilliseconds, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            return new ConnectivityCheckResult(false, stopwatch.ElapsedMilliseconds, "Connectivity check timed out.");
        }
        catch (Exception exception)
        {
            stopwatch.Stop();
            return new ConnectivityCheckResult(false, stopwatch.ElapsedMilliseconds, exception.Message);
        }
    }

    public async Task<AiModelView> UpsertModelAsync(
        UpsertModelCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command.Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.Model);
        var connection = await GetRequiredConnectionAsync(command.ProviderConnectionId, cancellationToken);
        var adapter = _registry.GetRequiredAdapter(connection.ProviderKind);
        if (!adapter.SupportsApiFormat(command.ApiFormat))
        {
            throw new NotSupportedException(
                $"Provider '{connection.Name}' does not support API format '{command.ApiFormat}'.");
        }

        AiModelProfile? existing = null;
        if (command.Id.HasValue)
        {
            existing = await GetRequiredModelAsync(command.Id.Value, cancellationToken);
            if (existing.ProviderConnectionId != command.ProviderConnectionId)
            {
                throw new InvalidOperationException(
                    $"AI model profile '{command.Id.Value}' does not belong to provider connection " +
                    $"'{command.ProviderConnectionId}'.");
            }
        }

        var now = DateTimeOffset.UtcNow;
        var profile = new AiModelProfile(
            existing?.Id ?? command.Id ?? Guid.NewGuid(),
            command.ProviderConnectionId,
            command.Name.Trim(),
            command.ApiFormat,
            command.Model.Trim(),
            command.Sampling ?? existing?.Sampling ?? new AiSamplingOptions(false, 0.7, false, 0.7),
            command.ModelOptions ?? existing?.ModelOptions ?? EmptyJsonDictionary(),
            existing?.CreatedAtUtc ?? now,
            now,
            command.Enabled ?? existing?.IsEnabled ?? true);
        await _repository.UpsertModelProfileAsync(profile, cancellationToken);
        return CreateModelView(profile);
    }

    public Task SetModelEnabledAsync(
        Guid modelProfileId,
        bool enabled,
        CancellationToken cancellationToken = default)
        => _repository.SetModelProfileEnabledAsync(modelProfileId, enabled, cancellationToken);

    public Task SetAllModelsEnabledAsync(
        Guid connectionId,
        bool enabled,
        CancellationToken cancellationToken = default)
        => _repository.SetAllModelProfilesEnabledAsync(connectionId, enabled, cancellationToken);

    public Task DeleteModelAsync(Guid modelProfileId, CancellationToken cancellationToken = default)
        => _repository.DeleteModelProfileAsync(modelProfileId, cancellationToken);

    public async Task SetDefaultModelAsync(
        string scope,
        Guid modelProfileId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        var enabledModels = await _repository.ListEnabledModelProfilesAsync(cancellationToken);
        if (enabledModels.All(profile => profile.Id != modelProfileId))
        {
            throw new InvalidOperationException(
                $"AI model profile '{modelProfileId}' is not enabled or its provider connection is disabled.");
        }

        await _repository.SetModelProfileSelectionAsync(
            new AiModelProfileSelection(scope.Trim(), modelProfileId, DateTimeOffset.UtcNow),
            cancellationToken);
    }

    public async Task<Guid?> GetDefaultModelAsync(string scope, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        var selection = await _repository.GetModelProfileSelectionAsync(scope.Trim(), cancellationToken);
        return selection?.ModelProfileId;
    }

    public async Task<IReadOnlyList<EnabledModelView>> ListEnabledModelsAsync(
        CancellationToken cancellationToken = default)
    {
        var profiles = await _repository.ListEnabledModelProfilesAsync(cancellationToken);
        var connections = (await _repository.ListAllProviderConnectionsAsync(cancellationToken))
            .ToDictionary(connection => connection.Id);

        return profiles
            .Where(profile => connections.ContainsKey(profile.ProviderConnectionId))
            .Select(profile => new EnabledModelView(
                profile.Id,
                profile.Name,
                profile.Model,
                connections[profile.ProviderConnectionId].Name))
            .ToArray();
    }

    private async Task<AiProviderView> CreateProviderViewAsync(
        AiProviderConnection connection,
        AiProviderCatalogEntry catalogEntry,
        IReadOnlyList<AiModelProfile> profiles,
        CancellationToken cancellationToken)
    {
        string? apiKey = null;
        if (connection.CredentialRefs.TryGetValue(ApiKeySecretName, out var apiKeyRef))
        {
            apiKey = await _secretProtector.RetrieveSecretAsync(apiKeyRef, cancellationToken);
        }

        var adapter = _registry.TryGetAdapter(connection.ProviderKind);
        var supportedFormats = adapter is null
            ? catalogEntry.SupportedFormats
            : ResolveSupportedFormats(adapter);
        var supportsModelListing = adapter?.SupportsModelListing ?? catalogEntry.SupportsModelListing;
        var defaultApiFormat = adapter is null
            ? catalogEntry.DefaultApiFormat
            : ResolveDefaultApiFormat(connection, catalogEntry, adapter);
        var modelViews = profiles.Select(CreateModelView).ToArray();
        return new AiProviderView(
            connection.Id,
            connection.CatalogId,
            connection.Name,
            catalogEntry.Subtitle,
            catalogEntry.AccentColor,
            connection.IsEnabled,
            true,
            !string.IsNullOrWhiteSpace(apiKey),
            string.IsNullOrWhiteSpace(apiKey) ? null : MaskApiKey(apiKey),
            connection.Endpoint.AbsoluteUri,
            connection.ProviderKind,
            connection.AuthKind,
            catalogEntry.GetApiKeyUrl,
            supportsModelListing,
            defaultApiFormat,
            supportedFormats,
            connection.ConnectionOptions,
            modelViews,
            modelViews.Length);
    }

    private static AiProviderView CreatePlaceholderView(AiProviderCatalogEntry catalogEntry)
    {
        return new AiProviderView(
            null,
            catalogEntry.CatalogId,
            catalogEntry.DisplayName,
            catalogEntry.Subtitle,
            catalogEntry.AccentColor,
            false,
            false,
            false,
            null,
            catalogEntry.DefaultEndpoint.AbsoluteUri,
            catalogEntry.ProviderKind,
            catalogEntry.AuthKind,
            catalogEntry.GetApiKeyUrl,
            catalogEntry.SupportsModelListing,
            catalogEntry.DefaultApiFormat,
            catalogEntry.SupportedFormats,
            EmptyJsonDictionary(),
            [],
            0);
    }

    /// <summary>Enumerates the wire formats an adapter can serve, ordered by the enum definition.</summary>
    private static IReadOnlyList<AiProviderApiFormat> ResolveSupportedFormats(IAiProviderAdapter adapter)
        => Enum.GetValues<AiProviderApiFormat>()
            .Where(adapter.SupportsApiFormat)
            .ToArray();

    /// <summary>
    /// Resolves the default wire format for a connection: the value persisted in
    /// connection options when present and still supported by the adapter,
    /// otherwise the catalog default, otherwise the adapter's first supported format.
    /// </summary>
    private static AiProviderApiFormat ResolveDefaultApiFormat(
        AiProviderConnection connection,
        AiProviderCatalogEntry catalogEntry,
        IAiProviderAdapter adapter)
    {
        if (connection.ConnectionOptions.TryGetValue(AiProviderProtocols.DefaultApiFormatOptionKey, out var element) &&
            element.ValueKind == JsonValueKind.Number &&
            element.TryGetInt32(out var raw) &&
            Enum.IsDefined((AiProviderApiFormat)raw) &&
            adapter.SupportsApiFormat((AiProviderApiFormat)raw))
        {
            return (AiProviderApiFormat)raw;
        }

        if (adapter.SupportsApiFormat(catalogEntry.DefaultApiFormat))
        {
            return catalogEntry.DefaultApiFormat;
        }

        return ResolveSupportedFormats(adapter)[0];
    }

    private static AiModelView CreateModelView(AiModelProfile profile)
        => new(
            profile.Id,
            profile.ProviderConnectionId,
            profile.Name,
            profile.Model,
            profile.ApiFormat,
            profile.Sampling,
            profile.ModelOptions,
            profile.IsEnabled,
            ReadInt64(profile.ModelOptions, ContextLengthKey),
            ReadInt64(profile.ModelOptions, MaxOutputTokensKey),
            ReadDecimal(profile.ModelOptions, PriceInKey),
            ReadDecimal(profile.ModelOptions, PriceOutKey),
            ReadDecimal(profile.ModelOptions, PriceCacheWriteKey),
            ReadDecimal(profile.ModelOptions, PriceCacheReadKey));

    private async Task<AiProviderConnection> GetRequiredConnectionAsync(
        Guid connectionId,
        CancellationToken cancellationToken)
        => await _repository.GetProviderConnectionAsync(connectionId, cancellationToken)
            ?? throw new KeyNotFoundException($"AI provider connection '{connectionId}' was not found.");

    private async Task<AiModelProfile> GetRequiredModelAsync(
        Guid modelProfileId,
        CancellationToken cancellationToken)
        => await _repository.GetModelProfileAsync(modelProfileId, cancellationToken)
            ?? throw new KeyNotFoundException($"AI model profile '{modelProfileId}' was not found.");

    private async Task<IReadOnlyDictionary<string, string>> ResolveSecretsAsync(
        AiProviderConnection connection,
        CancellationToken cancellationToken)
        => await AiProviderSecrets.ResolveAsync(_secretProtector, connection, cancellationToken);

    private static IReadOnlyDictionary<string, JsonElement> MergeDisplayMetadata(
        IReadOnlyDictionary<string, JsonElement> existingOptions,
        AiModelDescriptor descriptor,
        out bool changed)
    {
        var merged = existingOptions.ToDictionary(pair => pair.Key, pair => pair.Value.Clone(), StringComparer.Ordinal);
        changed = false;
        AddIfMissing(merged, DisplayNameKey, descriptor.DisplayName, ref changed);
        AddIfMissing(merged, ContextLengthKey, descriptor.ContextLength, ref changed);
        AddIfMissing(merged, MaxOutputTokensKey, descriptor.MaxOutputTokens, ref changed);
        AddIfMissing(merged, PriceInKey, descriptor.PriceInPerMTok, ref changed);
        AddIfMissing(merged, PriceOutKey, descriptor.PriceOutPerMTok, ref changed);
        AddIfMissing(merged, PriceCacheWriteKey, descriptor.PriceCacheWritePerMTok, ref changed);
        AddIfMissing(merged, PriceCacheReadKey, descriptor.PriceCacheReadPerMTok, ref changed);
        return merged;
    }

    private static void AddIfMissing<TValue>(
        IDictionary<string, JsonElement> options,
        string key,
        TValue? value,
        ref bool changed)
    {
        if (value is null || options.ContainsKey(key))
        {
            return;
        }

        options[key] = JsonSerializer.SerializeToElement(value);
        changed = true;
    }

    private static long? ReadInt64(IReadOnlyDictionary<string, JsonElement> options, string key)
        => options.TryGetValue(key, out var value) &&
           value.ValueKind == JsonValueKind.Number &&
           value.TryGetInt64(out var number)
            ? number
            : null;

    private static decimal? ReadDecimal(IReadOnlyDictionary<string, JsonElement> options, string key)
        => options.TryGetValue(key, out var value) &&
           value.ValueKind == JsonValueKind.Number &&
           value.TryGetDecimal(out var number)
            ? number
            : null;

    private static string MaskApiKey(string apiKey)
    {
        var prefix = apiKey.StartsWith("sk-", StringComparison.Ordinal) ? "sk-" : string.Empty;
        // Only echo a suffix when it reveals a small fraction of the key; short keys stay fully masked.
        return apiKey.Length >= 8 ? $"{prefix}****{apiKey[^4..]}" : $"{prefix}****";
    }

    /// <summary>
    /// Builds the persisted connection options. Starts from the command options
    /// (falling back to the existing connection's), then records the default wire
    /// format on create so the UI and remote-model merge can resolve it later. The
    /// default format is part of the connection's fixed protocol: on update the
    /// stored value is preserved and the command's format is ignored.
    /// </summary>
    private static IReadOnlyDictionary<string, JsonElement> ResolveConnectionOptions(
        SaveProviderCommand command,
        AiProviderConnection? existing,
        AiProviderKind providerKind,
        AiProviderCatalogEntry catalogEntry)
    {
        var options = (command.ConnectionOptions ?? existing?.ConnectionOptions ?? EmptyJsonDictionary())
            .ToDictionary(pair => pair.Key, pair => pair.Value.Clone(), StringComparer.Ordinal);

        // The protocol (and its default format) is fixed once the connection exists.
        // Only stamp the default format on create; on update keep whatever is stored.
        if (existing is null)
        {
            var defaultApiFormat = command.DefaultApiFormat
                ?? (providerKind == catalogEntry.ProviderKind ? catalogEntry.DefaultApiFormat : (AiProviderApiFormat?)null);
            if (defaultApiFormat is { } format && Enum.IsDefined(format))
            {
                options[AiProviderProtocols.DefaultApiFormatOptionKey] =
                    JsonSerializer.SerializeToElement((int)format);
            }
        }

        return options;
    }

    private static IReadOnlyDictionary<string, JsonElement> EmptyJsonDictionary()
        => new Dictionary<string, JsonElement>(StringComparer.Ordinal);
}
