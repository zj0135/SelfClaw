using System.Runtime.CompilerServices;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.AI;
using SelfClaw.Core.Interfaces;
using SelfClaw.Infrastructure.AiProviders;
using SelfClaw.Infrastructure.AiProviders.Abstractions;
using SelfClaw.Infrastructure.AiProviders.Models;
using SelfClaw.Infrastructure.AiProviders.Models.Views;

namespace SelfClaw.Tests.Infrastructure.AiProviders;

public sealed class AiProviderSettingsServiceTests
{
    [Fact]
    public async Task GetState_returns_catalog_placeholders_and_only_a_masked_api_key()
    {
        var repository = new InMemoryAiProviderRepository();
        var protector = new FakeSecretProtector();
        var connection = CreateConnection("openai", AiProviderKind.OpenAI, "OpenAI Team");
        connection = connection with
        {
            CredentialRefs = new Dictionary<string, string> { ["api_key"] = "secret:openai" }
        };
        repository.Connections[connection.Id] = connection;
        protector.Secrets["secret:openai"] = "sk-test-abcdef";
        var profile = CreateProfile(connection.Id, "gpt-4.1", enabled: false) with
        {
            ModelOptions = JsonObject("{\"display.contextLength\":1048576,\"display.priceInPerMTok\":2.0}")
        };
        repository.Models[profile.Id] = profile;
        repository.Selections[AiModelSelectionScopes.DesktopDefault] =
            new AiModelProfileSelection(AiModelSelectionScopes.DesktopDefault, profile.Id, DateTimeOffset.UtcNow);
        var service = CreateService(repository, protector, new FakeAiProviderAdapter(AiProviderKind.OpenAI));

        var state = await service.GetStateAsync();

        state.Providers.Should().HaveCount(8);
        state.DefaultModelProfileId.Should().Be(profile.Id);
        var openAi = state.Providers.Single(provider => provider.CatalogId == "openai");
        openAi.ConnectionId.Should().Be(connection.Id);
        openAi.IsConfigured.Should().BeTrue();
        openAi.HasApiKey.Should().BeTrue();
        openAi.KeyMask.Should().Be("sk-****cdef");
        openAi.KeyMask.Should().NotContain("test-ab");
        openAi.Models.Should().ContainSingle();
        openAi.Models[0].Enabled.Should().BeFalse();
        openAi.Models[0].ContextLength.Should().Be(1048576);
        openAi.Models[0].PriceInPerMTok.Should().Be(2m);

        var ollama = state.Providers.Single(provider => provider.CatalogId == "ollama");
        ollama.ConnectionId.Should().BeNull();
        ollama.IsConfigured.Should().BeFalse();
        ollama.Enabled.Should().BeFalse();
        ollama.Base.Should().Be("http://localhost:11434/");
        JsonSerializer.Serialize(state).Should().NotContain("sk-test-abcdef");
    }

    [Fact]
    public async Task SaveProvider_applies_api_key_three_state_rule_and_catalog_contract()
    {
        var repository = new InMemoryAiProviderRepository();
        var protector = new FakeSecretProtector();
        var existing = CreateConnection("custom", AiProviderKind.OpenAICompatible, "Gateway") with
        {
            CredentialRefs = new Dictionary<string, string> { ["api_key"] = "secret:existing" }
        };
        repository.Connections[existing.Id] = existing;
        protector.Secrets["secret:existing"] = "old-secret";
        var service = CreateService(repository, protector, new FakeAiProviderAdapter(AiProviderKind.OpenAI));

        await service.SaveProviderAsync(new SaveProviderCommand(
            existing.Id,
            "openai",
            "OpenAI Updated",
            new Uri("https://proxy.example/v1/"),
            ApiKey: null,
            ConnectionOptions: null));

        protector.StoreCalls.Should().BeEmpty();
        protector.DeleteCalls.Should().BeEmpty();
        repository.Connections[existing.Id].CredentialRefs["api_key"].Should().Be("secret:existing");
        // Protocol and catalog membership are fixed once a connection exists: an update that
        // carries no provider kind must not reset them, even if the command names a different catalog.
        repository.Connections[existing.Id].CatalogId.Should().Be("custom");
        repository.Connections[existing.Id].ProviderKind.Should().Be(AiProviderKind.OpenAICompatible);
        repository.Connections[existing.Id].AuthKind.Should().Be(AiProviderAuthKind.ApiKey);

        await service.SaveProviderAsync(new SaveProviderCommand(
            existing.Id,
            "openai",
            "OpenAI Updated",
            new Uri("https://proxy.example/v1/"),
            "replacement-secret",
            ConnectionOptions: null));

        protector.StoreCalls.Should().ContainSingle().Which.ExistingSecretRef.Should().Be("secret:existing");
        protector.Secrets["secret:existing"].Should().Be("replacement-secret");

        await service.SaveProviderAsync(new SaveProviderCommand(
            existing.Id,
            "openai",
            "OpenAI Updated",
            new Uri("https://proxy.example/v1/"),
            string.Empty,
            ConnectionOptions: null));

        protector.DeleteCalls.Should().Contain("secret:existing");
        repository.Connections[existing.Id].CredentialRefs.Should().NotContainKey("api_key");

        var ollama = await service.SaveProviderAsync(new SaveProviderCommand(
            null,
            "ollama",
            "Local Ollama",
            new Uri("http://localhost:11434/"),
            "must-not-be-stored",
            ConnectionOptions: null));
        ollama.ProviderKind.Should().Be(AiProviderKind.Ollama);
        ollama.AuthKind.Should().Be(AiProviderAuthKind.None);
        ollama.HasApiKey.Should().BeFalse();
        protector.Secrets.Values.Should().NotContain("must-not-be-stored");
    }

    [Fact]
    public async Task DeleteProvider_deletes_all_secret_files_before_the_database_row()
    {
        var operations = new List<string>();
        var repository = new InMemoryAiProviderRepository(operations);
        var protector = new FakeSecretProtector(operations);
        var connection = CreateConnection("custom", AiProviderKind.OpenAICompatible, "Gateway") with
        {
            CredentialRefs = new Dictionary<string, string>
            {
                ["api_key"] = "secret:key",
                ["secondary"] = "secret:secondary"
            }
        };
        repository.Connections[connection.Id] = connection;
        protector.Secrets["secret:key"] = "key";
        protector.Secrets["secret:secondary"] = "secondary";
        var service = CreateService(repository, protector, new FakeAiProviderAdapter(AiProviderKind.OpenAICompatible));

        await service.DeleteProviderAsync(connection.Id);

        operations.Should().Equal(
            "secret.delete:secret:key",
            "secret.delete:secret:secondary",
            $"provider.delete:{connection.Id:D}");
        repository.Connections.Should().NotContainKey(connection.Id);
        protector.Secrets.Should().BeEmpty();
    }

    [Fact]
    public async Task FetchAndMergeRemoteModels_adds_disabled_fills_missing_metadata_and_never_deletes()
    {
        var repository = new InMemoryAiProviderRepository();
        var protector = new FakeSecretProtector();
        var connection = CreateConnection("openai", AiProviderKind.OpenAI, "OpenAI");
        repository.Connections[connection.Id] = connection;
        var existing = CreateProfile(connection.Id, "existing-model", enabled: true) with
        {
            Name = "User display name",
            Sampling = new AiSamplingOptions(true, 0.2, true, 0.9),
            ModelOptions = JsonObject("{\"display.contextLength\":111,\"user.option\":true}")
        };
        var localOnly = CreateProfile(connection.Id, "local-only", enabled: false);
        repository.Models[existing.Id] = existing;
        repository.Models[localOnly.Id] = localOnly;
        var adapter = new FakeAiProviderAdapter(AiProviderKind.OpenAI)
        {
            RemoteModels =
            [
                new AiModelDescriptor("existing-model", "Remote name", 999, 222, 1.25m, 5m, null, 0.25m),
                new AiModelDescriptor("new-model", "New remote model", 400000, 32000, 2m, 8m, 2.5m, 0.5m)
            ]
        };
        var service = CreateService(repository, protector, adapter);

        var result = await service.FetchAndMergeRemoteModelsAsync(connection.Id);

        result.Should().HaveCount(3);
        repository.Models.Values.Should().Contain(profile => profile.Model == "local-only");
        var updatedExisting = repository.Models[existing.Id];
        updatedExisting.Name.Should().Be("User display name");
        updatedExisting.Sampling.Should().Be(existing.Sampling);
        updatedExisting.ApiFormat.Should().Be(existing.ApiFormat);
        updatedExisting.IsEnabled.Should().BeTrue();
        updatedExisting.ModelOptions["display.contextLength"].GetInt64().Should().Be(111);
        updatedExisting.ModelOptions["display.maxOutputTokens"].GetInt64().Should().Be(222);
        updatedExisting.ModelOptions["display.priceInPerMTok"].GetDecimal().Should().Be(1.25m);
        updatedExisting.ModelOptions["user.option"].GetBoolean().Should().BeTrue();

        var added = repository.Models.Values.Single(profile => profile.Model == "new-model");
        added.Name.Should().Be("New remote model");
        added.ApiFormat.Should().Be(AiProviderApiFormat.OpenAIResponses);
        added.IsEnabled.Should().BeFalse();
        added.ModelOptions["display.contextLength"].GetInt64().Should().Be(400000);
        added.ModelOptions["display.priceCacheWritePerMTok"].GetDecimal().Should().Be(2.5m);
    }

    [Fact]
    public async Task CheckConnectivity_sends_one_token_ping_and_returns_success_or_original_error()
    {
        var repository = new InMemoryAiProviderRepository();
        var protector = new FakeSecretProtector();
        var connection = CreateConnection("openai", AiProviderKind.OpenAI, "OpenAI") with
        {
            CredentialRefs = new Dictionary<string, string> { ["api_key"] = "secret:key" }
        };
        var profile = CreateProfile(connection.Id, "gpt-4.1", enabled: true);
        repository.Connections[connection.Id] = connection;
        repository.Models[profile.Id] = profile;
        protector.Secrets["secret:key"] = "plain-key";
        var client = new FakeChatClient();
        var adapter = new FakeAiProviderAdapter(AiProviderKind.OpenAI) { ChatClient = client };
        var service = CreateService(repository, protector, adapter);

        var success = await service.CheckConnectivityAsync(connection.Id, profile.Id);

        success.Ok.Should().BeTrue();
        success.ErrorMessage.Should().BeNull();
        client.LastOptions.Should().NotBeNull();
        client.LastOptions!.MaxOutputTokens.Should().Be(1);
        client.LastMessages.Should().ContainSingle().Which.Text.Should().Be("ping");
        adapter.LastSecrets.Should().ContainKey("api_key").WhoseValue.Should().Be("plain-key");

        client.ResponseException = new InvalidOperationException("provider unavailable");
        var failure = await service.CheckConnectivityAsync(connection.Id, profile.Id);

        failure.Ok.Should().BeFalse();
        failure.ErrorMessage.Should().Be("provider unavailable");
        failure.LatencyMs.Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public async Task Enabled_models_and_default_selection_require_both_model_and_provider_enabled()
    {
        var repository = new InMemoryAiProviderRepository();
        var protector = new FakeSecretProtector();
        var enabledConnection = CreateConnection("openai", AiProviderKind.OpenAI, "OpenAI");
        var disabledConnection = CreateConnection("custom", AiProviderKind.OpenAICompatible, "Disabled gateway") with
        {
            IsEnabled = false
        };
        repository.Connections[enabledConnection.Id] = enabledConnection;
        repository.Connections[disabledConnection.Id] = disabledConnection;
        var enabledModel = CreateProfile(enabledConnection.Id, "enabled", enabled: true);
        var disabledModel = CreateProfile(enabledConnection.Id, "disabled", enabled: false);
        var hiddenByProvider = CreateProfile(disabledConnection.Id, "hidden", enabled: true);
        repository.Models[enabledModel.Id] = enabledModel;
        repository.Models[disabledModel.Id] = disabledModel;
        repository.Models[hiddenByProvider.Id] = hiddenByProvider;
        var service = CreateService(repository, protector, new FakeAiProviderAdapter(AiProviderKind.OpenAI));

        var enabledViews = await service.ListEnabledModelsAsync();

        enabledViews.Should().ContainSingle().Which.ModelProfileId.Should().Be(enabledModel.Id);
        await service.SetDefaultModelAsync("desktop-default", enabledModel.Id);
        repository.Selections["desktop-default"].ModelProfileId.Should().Be(enabledModel.Id);

        var act = () => service.SetDefaultModelAsync("desktop-default", disabledModel.Id);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*not enabled*");
    }

    [Fact]
    public async Task Provider_and_model_commands_persist_enable_disable_and_delete_changes()
    {
        var repository = new InMemoryAiProviderRepository();
        var protector = new FakeSecretProtector();
        var connection = CreateConnection("custom", AiProviderKind.OpenAICompatible, "Gateway");
        repository.Connections[connection.Id] = connection;
        var service = CreateService(
            repository,
            protector,
            new FakeAiProviderAdapter(AiProviderKind.OpenAICompatible)
            {
                Formats = [AiProviderApiFormat.OpenAIChatCompletions, AiProviderApiFormat.OpenAIResponses]
            });

        var created = await service.UpsertModelAsync(new UpsertModelCommand(
            null,
            connection.Id,
            "Manual model",
            AiProviderApiFormat.OpenAIChatCompletions,
            "manual-model",
            new AiSamplingOptions(true, 0.3, false, 0.7),
            JsonObject("{\"max_tokens\":1024}"),
            Enabled: false));

        created.Enabled.Should().BeFalse();
        repository.Models[created.ModelProfileId].Sampling.Temperature.Should().Be(0.3);

        await service.SetModelEnabledAsync(created.ModelProfileId, true);
        repository.Models[created.ModelProfileId].IsEnabled.Should().BeTrue();

        await service.SetAllModelsEnabledAsync(connection.Id, false);
        repository.Models[created.ModelProfileId].IsEnabled.Should().BeFalse();

        await service.SetProviderEnabledAsync(connection.Id, false);
        repository.Connections[connection.Id].IsEnabled.Should().BeFalse();

        var invalid = () => service.UpsertModelAsync(new UpsertModelCommand(
            null,
            connection.Id,
            "Invalid protocol",
            AiProviderApiFormat.AnthropicMessages,
            "invalid",
            Sampling: null,
            ModelOptions: null));
        await invalid.Should().ThrowAsync<NotSupportedException>().WithMessage("*AnthropicMessages*");

        await service.DeleteModelAsync(created.ModelProfileId);
        repository.Models.Should().NotContainKey(created.ModelProfileId);
    }

    [Fact]
    public async Task UpsertModel_edit_without_enabled_preserves_the_stored_enable_state()
    {
        var repository = new InMemoryAiProviderRepository();
        var protector = new FakeSecretProtector();
        var connection = CreateConnection("custom", AiProviderKind.OpenAICompatible, "Gateway");
        repository.Connections[connection.Id] = connection;
        var service = CreateService(
            repository,
            protector,
            new FakeAiProviderAdapter(AiProviderKind.OpenAICompatible));

        var created = await service.UpsertModelAsync(new UpsertModelCommand(
            null,
            connection.Id,
            "Manual model",
            AiProviderApiFormat.OpenAIChatCompletions,
            "manual-model",
            Sampling: null,
            ModelOptions: null,
            Enabled: false));
        created.Enabled.Should().BeFalse();

        // Editing the name only (Enabled omitted) must not silently re-enable the disabled model.
        var edited = await service.UpsertModelAsync(new UpsertModelCommand(
            created.ModelProfileId,
            connection.Id,
            "Renamed model",
            AiProviderApiFormat.OpenAIChatCompletions,
            "manual-model",
            Sampling: null,
            ModelOptions: null,
            Enabled: null));

        edited.Name.Should().Be("Renamed model");
        edited.Enabled.Should().BeFalse();
        repository.Models[created.ModelProfileId].IsEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task GetState_masks_short_api_keys_without_echoing_them()
    {
        var repository = new InMemoryAiProviderRepository();
        var protector = new FakeSecretProtector();
        var connection = CreateConnection("custom", AiProviderKind.OpenAICompatible, "Gateway");
        var secretRef = await protector.StoreSecretAsync("abc", existingSecretRef: null);
        repository.Connections[connection.Id] = connection with
        {
            CredentialRefs = new Dictionary<string, string> { ["api_key"] = secretRef }
        };
        var service = CreateService(repository, protector, new FakeAiProviderAdapter(AiProviderKind.OpenAICompatible));

        var state = await service.GetStateAsync();

        var provider = state.Providers.Single(view => view.ConnectionId == connection.Id);
        provider.HasApiKey.Should().BeTrue();
        provider.KeyMask.Should().Be("****");
        provider.KeyMask.Should().NotContain("abc");
    }

    [Fact]
    public async Task GetState_ignores_non_numeric_display_metadata_instead_of_throwing()
    {
        var repository = new InMemoryAiProviderRepository();
        var protector = new FakeSecretProtector();
        var connection = CreateConnection("custom", AiProviderKind.OpenAICompatible, "Gateway");
        repository.Connections[connection.Id] = connection;
        var profile = CreateProfile(connection.Id, "manual-model", enabled: true) with
        {
            ModelOptions = JsonObject("{\"display.contextLength\":\"lots\",\"display.priceInPerMTok\":\"free\"}")
        };
        repository.Models[profile.Id] = profile;
        var service = CreateService(repository, protector, new FakeAiProviderAdapter(AiProviderKind.OpenAICompatible));

        var state = await service.GetStateAsync();

        var model = state.Providers.Single(view => view.ConnectionId == connection.Id).Models.Single();
        model.ContextLength.Should().BeNull();
        model.PriceInPerMTok.Should().BeNull();
    }

    [Fact]
    public async Task GetState_exposes_curated_custom_protocol_options()
    {
        var repository = new InMemoryAiProviderRepository();
        var protector = new FakeSecretProtector();
        var service = CreateService(repository, protector, new FakeAiProviderAdapter(AiProviderKind.OpenAI));

        var state = await service.GetStateAsync();

        state.CustomProtocols.Should().NotBeEmpty();
        state.CustomProtocols.Select(protocol => protocol.Id)
            .Should().Contain(["openai-chat", "openai-responses", "anthropic", "ollama"]);
        var ollama = state.CustomProtocols.Single(protocol => protocol.Id == "ollama");
        ollama.ProviderKind.Should().Be(AiProviderKind.Ollama);
        ollama.AuthKind.Should().Be(AiProviderAuthKind.None);
        ollama.DefaultApiFormat.Should().Be(AiProviderApiFormat.OllamaNative);
    }

    [Fact]
    public async Task SaveProvider_creates_multiple_custom_connections_with_chosen_protocol()
    {
        var repository = new InMemoryAiProviderRepository();
        var protector = new FakeSecretProtector();
        var service = CreateService(
            repository,
            protector,
            new FakeAiProviderAdapter(AiProviderKind.OpenAICompatible)
            {
                Formats = [AiProviderApiFormat.OpenAIChatCompletions, AiProviderApiFormat.OpenAIResponses]
            },
            new FakeAiProviderAdapter(AiProviderKind.Anthropic)
            {
                Formats = [AiProviderApiFormat.AnthropicMessages]
            });

        var first = await service.SaveProviderAsync(new SaveProviderCommand(
            null,
            "custom",
            "Gateway One",
            new Uri("https://one.example/v1/"),
            "key-one",
            ConnectionOptions: null,
            ProviderKind: AiProviderKind.OpenAICompatible,
            DefaultApiFormat: AiProviderApiFormat.OpenAIChatCompletions));

        var second = await service.SaveProviderAsync(new SaveProviderCommand(
            null,
            "custom",
            "Gateway Two",
            new Uri("https://two.example/v1/"),
            "key-two",
            ConnectionOptions: null,
            ProviderKind: AiProviderKind.Anthropic,
            DefaultApiFormat: AiProviderApiFormat.AnthropicMessages));

        first.ConnectionId.Should().NotBeNull();
        second.ConnectionId.Should().NotBeNull();
        first.ConnectionId.Should().NotBe(second.ConnectionId!.Value);
        repository.Connections.Should().HaveCount(2);
        first.ProviderKind.Should().Be(AiProviderKind.OpenAICompatible);
        first.DefaultApiFormat.Should().Be(AiProviderApiFormat.OpenAIChatCompletions);
        second.ProviderKind.Should().Be(AiProviderKind.Anthropic);
        second.DefaultApiFormat.Should().Be(AiProviderApiFormat.AnthropicMessages);
        second.SupportedFormats.Should().ContainSingle().Which.Should().Be(AiProviderApiFormat.AnthropicMessages);

        var state = await service.GetStateAsync();
        state.Providers.Count(view => view.CatalogId == "custom" && view.IsConfigured).Should().Be(2);
    }

    [Fact]
    public async Task SaveProvider_keeps_the_original_protocol_when_a_later_edit_omits_it()
    {
        var repository = new InMemoryAiProviderRepository();
        var protector = new FakeSecretProtector();
        var service = CreateService(
            repository,
            protector,
            new FakeAiProviderAdapter(AiProviderKind.OpenAICompatible),
            new FakeAiProviderAdapter(AiProviderKind.Anthropic));

        var created = await service.SaveProviderAsync(new SaveProviderCommand(
            null,
            "custom",
            "Anthropic Gateway",
            new Uri("https://anthropic.example/"),
            "key",
            ConnectionOptions: null,
            ProviderKind: AiProviderKind.Anthropic,
            DefaultApiFormat: AiProviderApiFormat.AnthropicMessages));

        // An api-key or base-url edit carries no protocol and must not reset the connection.
        var edited = await service.SaveProviderAsync(new SaveProviderCommand(
            created.ConnectionId,
            "custom",
            "Anthropic Gateway",
            new Uri("https://anthropic.example/v2/"),
            ApiKey: null,
            ConnectionOptions: null,
            ProviderKind: AiProviderKind.OpenAICompatible,
            DefaultApiFormat: AiProviderApiFormat.OpenAIChatCompletions));

        edited.ProviderKind.Should().Be(AiProviderKind.Anthropic);
        edited.DefaultApiFormat.Should().Be(AiProviderApiFormat.AnthropicMessages);
        repository.Connections[created.ConnectionId!.Value].ProviderKind.Should().Be(AiProviderKind.Anthropic);
    }

    [Fact]
    public async Task UpsertModel_accepts_any_format_the_adapter_supports_and_rejects_the_rest()
    {
        var repository = new InMemoryAiProviderRepository();
        var protector = new FakeSecretProtector();
        // A custom connection whose kind is Ollama supports OllamaNative and OpenAIChatCompletions.
        var connection = CreateConnection("custom", AiProviderKind.Ollama, "Local");
        repository.Connections[connection.Id] = connection;
        var service = CreateService(
            repository,
            protector,
            new FakeAiProviderAdapter(AiProviderKind.Ollama)
            {
                Formats = [AiProviderApiFormat.OllamaNative, AiProviderApiFormat.OpenAIChatCompletions]
            });

        var native = await service.UpsertModelAsync(new UpsertModelCommand(
            null,
            connection.Id,
            "Native model",
            AiProviderApiFormat.OllamaNative,
            "llama3",
            Sampling: null,
            ModelOptions: null));
        native.ApiFormat.Should().Be(AiProviderApiFormat.OllamaNative);

        var invalid = () => service.UpsertModelAsync(new UpsertModelCommand(
            null,
            connection.Id,
            "Wrong protocol",
            AiProviderApiFormat.AnthropicMessages,
            "llama3",
            Sampling: null,
            ModelOptions: null));
        await invalid.Should().ThrowAsync<NotSupportedException>().WithMessage("*AnthropicMessages*");
    }

    private static AiProviderSettingsService CreateService(
        InMemoryAiProviderRepository repository,
        FakeSecretProtector protector,
        params IAiProviderAdapter[] adapters)
        => new(repository, new AiProviderRegistry(adapters), protector);

    private static AiProviderConnection CreateConnection(
        string catalogId,
        AiProviderKind providerKind,
        string name)
    {
        var now = DateTimeOffset.UtcNow;
        return new AiProviderConnection(
            Guid.NewGuid(),
            catalogId,
            name,
            providerKind,
            new Uri("https://api.example.test/v1/"),
            AiProviderAuthKind.ApiKey,
            new Dictionary<string, string>(),
            JsonObject("{}"),
            now,
            now);
    }

    private static AiModelProfile CreateProfile(Guid connectionId, string model, bool enabled)
    {
        var now = DateTimeOffset.UtcNow;
        return new AiModelProfile(
            Guid.NewGuid(),
            connectionId,
            model,
            AiProviderApiFormat.OpenAIResponses,
            model,
            new AiSamplingOptions(false, 0.7, false, 0.7),
            JsonObject("{}"),
            now,
            now,
            enabled);
    }

    private static IReadOnlyDictionary<string, JsonElement> JsonObject(string json)
        => JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json) ?? [];

    private sealed class FakeSecretProtector : ISecretProtector
    {
        private readonly IList<string>? _operations;

        public FakeSecretProtector(IList<string>? operations = null)
        {
            _operations = operations;
        }

        public Dictionary<string, string> Secrets { get; } = new(StringComparer.Ordinal);
        public List<StoreCall> StoreCalls { get; } = [];
        public List<string> DeleteCalls { get; } = [];

        public Task<string> StoreSecretAsync(
            string secret,
            string? existingSecretRef = null,
            CancellationToken cancellationToken = default)
        {
            var secretRef = existingSecretRef ?? $"secret:{Guid.NewGuid():D}";
            StoreCalls.Add(new StoreCall(secret, existingSecretRef, secretRef));
            Secrets[secretRef] = secret;
            _operations?.Add($"secret.store:{secretRef}");
            return Task.FromResult(secretRef);
        }

        public Task<string?> RetrieveSecretAsync(
            string secretRef,
            CancellationToken cancellationToken = default)
            => Task.FromResult(Secrets.TryGetValue(secretRef, out var secret) ? secret : null);

        public Task DeleteSecretAsync(string secretRef, CancellationToken cancellationToken = default)
        {
            DeleteCalls.Add(secretRef);
            Secrets.Remove(secretRef);
            _operations?.Add($"secret.delete:{secretRef}");
            return Task.CompletedTask;
        }
    }

    private sealed record StoreCall(string Secret, string? ExistingSecretRef, string ResultSecretRef);

    private sealed class InMemoryAiProviderRepository : IAiProviderRepository
    {
        private readonly IList<string>? _operations;

        public InMemoryAiProviderRepository(IList<string>? operations = null)
        {
            _operations = operations;
        }

        public Dictionary<Guid, AiProviderConnection> Connections { get; } = [];
        public Dictionary<Guid, AiModelProfile> Models { get; } = [];
        public Dictionary<string, AiModelProfileSelection> Selections { get; } = new(StringComparer.Ordinal);

        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<AiProviderConnection>> ListProviderConnectionsAsync(
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<AiProviderConnection>>(
                Connections.Values.Where(connection => connection.IsEnabled).ToArray());

        public Task<IReadOnlyList<AiProviderConnection>> ListAllProviderConnectionsAsync(
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<AiProviderConnection>>(Connections.Values.ToArray());

        public Task<AiProviderConnection?> GetProviderConnectionAsync(
            Guid providerConnectionId,
            CancellationToken cancellationToken = default)
            => Task.FromResult(Connections.GetValueOrDefault(providerConnectionId));

        public Task<AiProviderConnection> UpsertProviderConnectionAsync(
            AiProviderConnection connection,
            CancellationToken cancellationToken = default)
        {
            Connections[connection.Id] = connection;
            _operations?.Add($"provider.upsert:{connection.Id:D}");
            return Task.FromResult(connection);
        }

        public Task SetProviderConnectionEnabledAsync(
            Guid providerConnectionId,
            bool isEnabled,
            CancellationToken cancellationToken = default)
        {
            if (Connections.TryGetValue(providerConnectionId, out var connection))
            {
                Connections[providerConnectionId] = connection with
                {
                    IsEnabled = isEnabled,
                    UpdatedAtUtc = DateTimeOffset.UtcNow
                };
            }

            return Task.CompletedTask;
        }

        public Task DeleteProviderConnectionAsync(
            Guid providerConnectionId,
            CancellationToken cancellationToken = default)
        {
            Connections.Remove(providerConnectionId);
            foreach (var modelId in Models.Values
                .Where(model => model.ProviderConnectionId == providerConnectionId)
                .Select(model => model.Id)
                .ToArray())
            {
                Models.Remove(modelId);
            }

            foreach (var scope in Selections
                .Where(selection => !Models.ContainsKey(selection.Value.ModelProfileId))
                .Select(selection => selection.Key)
                .ToArray())
            {
                Selections.Remove(scope);
            }

            _operations?.Add($"provider.delete:{providerConnectionId:D}");
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<AiModelProfile>> ListModelProfilesAsync(
            Guid? providerConnectionId = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<AiModelProfile>>(Models.Values
                .Where(model => !providerConnectionId.HasValue || model.ProviderConnectionId == providerConnectionId)
                .ToArray());

        public Task<AiModelProfile?> GetModelProfileAsync(
            Guid modelProfileId,
            CancellationToken cancellationToken = default)
            => Task.FromResult(Models.GetValueOrDefault(modelProfileId));

        public Task<AiModelProfile> UpsertModelProfileAsync(
            AiModelProfile profile,
            CancellationToken cancellationToken = default)
        {
            Models[profile.Id] = profile;
            return Task.FromResult(profile);
        }

        public Task SetModelProfileEnabledAsync(
            Guid modelProfileId,
            bool isEnabled,
            CancellationToken cancellationToken = default)
        {
            if (Models.TryGetValue(modelProfileId, out var model))
            {
                Models[modelProfileId] = model with { IsEnabled = isEnabled, UpdatedAtUtc = DateTimeOffset.UtcNow };
            }

            return Task.CompletedTask;
        }

        public Task SetAllModelProfilesEnabledAsync(
            Guid providerConnectionId,
            bool isEnabled,
            CancellationToken cancellationToken = default)
        {
            foreach (var model in Models.Values
                .Where(model => model.ProviderConnectionId == providerConnectionId)
                .ToArray())
            {
                Models[model.Id] = model with { IsEnabled = isEnabled, UpdatedAtUtc = DateTimeOffset.UtcNow };
            }

            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<AiModelProfile>> ListEnabledModelProfilesAsync(
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<AiModelProfile>>(Models.Values
                .Where(model => model.IsEnabled &&
                    Connections.TryGetValue(model.ProviderConnectionId, out var connection) &&
                    connection.IsEnabled)
                .ToArray());

        public Task DeleteModelProfileAsync(Guid modelProfileId, CancellationToken cancellationToken = default)
        {
            Models.Remove(modelProfileId);
            return Task.CompletedTask;
        }

        public Task<AiModelProfileSelection?> GetModelProfileSelectionAsync(
            string scope,
            CancellationToken cancellationToken = default)
            => Task.FromResult(Selections.GetValueOrDefault(scope));

        public Task<AiModelProfileSelection> SetModelProfileSelectionAsync(
            AiModelProfileSelection selection,
            CancellationToken cancellationToken = default)
        {
            Selections[selection.Scope] = selection;
            return Task.FromResult(selection);
        }
    }

    private sealed class FakeAiProviderAdapter : IAiProviderAdapter
    {
        public FakeAiProviderAdapter(AiProviderKind providerKind)
        {
            ProviderKind = providerKind;
        }

        public AiProviderKind ProviderKind { get; }
        public bool SupportsModelListing { get; init; } = true;
        public IReadOnlyList<AiModelDescriptor> RemoteModels { get; init; } = [];
        public FakeChatClient ChatClient { get; init; } = new();
        public IReadOnlyDictionary<string, string> LastSecrets { get; private set; } =
            new Dictionary<string, string>();

        /// <summary>
        /// When set, restricts the formats this fake advertises. Left null the fake
        /// supports every format (the historical behavior most tests rely on).
        /// </summary>
        public IReadOnlyList<AiProviderApiFormat>? Formats { get; init; }

        public bool SupportsApiFormat(AiProviderApiFormat apiFormat)
            => Formats is null || Formats.Contains(apiFormat);

        public Task<IReadOnlyList<AiModelDescriptor>> ListModelsAsync(
            AiProviderConnection connection,
            IReadOnlyDictionary<string, string> secrets,
            CancellationToken cancellationToken = default)
        {
            LastSecrets = secrets;
            return Task.FromResult(RemoteModels);
        }

        public IChatClient CreateChatClient(AiProviderClientRequest request)
        {
            LastSecrets = request.Secrets;
            return ChatClient;
        }

        public ChatOptions CreateChatOptions(AiProviderClientRequest request) => new();
    }

    private sealed class FakeChatClient : IChatClient
    {
        public Exception? ResponseException { get; set; }
        public IReadOnlyList<ChatMessage> LastMessages { get; private set; } = [];
        public ChatOptions? LastOptions { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            LastMessages = messages.ToArray();
            LastOptions = options;
            if (ResponseException is not null)
            {
                return Task.FromException<ChatResponse>(ResponseException);
            }

            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "pong")));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
