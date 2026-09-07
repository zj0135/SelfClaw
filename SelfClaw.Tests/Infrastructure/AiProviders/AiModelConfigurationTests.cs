using System.Text.Json;
using System.Net;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using SelfClaw.Core.Interfaces;
using SelfClaw.Infrastructure.AiProviders;
using SelfClaw.Infrastructure.AiProviders.Models;
using SelfClaw.Infrastructure.AiProviders.Models.Views;
using SelfClaw.Infrastructure.AiProviders.Http;
using SelfClaw.Infrastructure.AiProviders.OpenAi;
using SelfClaw.Infrastructure.Data.Sqlite;
using SelfClaw.Infrastructure.Data.Sqlite.Repositories;
using SelfClaw.Infrastructure.Options;

namespace SelfClaw.Tests.Infrastructure.AiProviders;

public sealed class AiModelConfigurationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "SelfClawTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Saved_configuration_survives_restart_and_provider_deletion_and_is_reused_by_exact_model_id()
    {
        var repository = CreateRepository();
        var service = CreateService(repository);
        var first = await CreateProfileAsync(repository, "model-A");
        var second = await CreateProfileAsync(repository, "model-A");
        var differentCase = await CreateProfileAsync(repository, "model-a");
        var configuration = CreateConfiguration("model-A");

        await service.SaveModelConfigurationAsync(configuration);

        (await repository.GetModelProfileAsync(first.Id))?.Configuration.Should().BeEquivalentTo(configuration);
        (await repository.ListModelProfilesAsync(second.ProviderConnectionId)).Single().Configuration.Should().BeEquivalentTo(configuration);
        (await repository.ListEnabledModelProfilesAsync()).Single(profile => profile.Id == second.Id)
            .Configuration.Should().BeEquivalentTo(configuration);
        (await repository.GetModelProfileAsync(differentCase.Id))?.Configuration.Should().BeNull();

        var restarted = CreateRepository();
        (await restarted.ListAsync()).Should().ContainSingle().Which.Should().BeEquivalentTo(configuration);
        await restarted.DeleteProviderConnectionAsync(first.ProviderConnectionId);
        await restarted.DeleteProviderConnectionAsync(second.ProviderConnectionId);
        (await restarted.ListAsync()).Should().ContainSingle();

        var newProfile = await CreateProfileAsync(restarted, "model-A");
        var restartedService = CreateService(restarted);
        var view = await restartedService.UpsertModelAsync(new UpsertModelCommand(
            newProfile.Id, newProfile.ProviderConnectionId, "Provider alias", newProfile.ApiFormat, newProfile.Model,
            null, null, true));
        view.Configuration.Should().BeEquivalentTo(configuration);
        view.Sampling.Should().BeEquivalentTo(configuration.Sampling);
        view.ContextLength.Should().Be(128000);
        view.MaxOutputTokens.Should().Be(16000);
        view.PriceInPerMTok.Should().Be(0.123456m);

        var factory = new AiChatClientFactory(restarted, new AiProviderRegistry([new OpenAiProviderAdapter()]), new TestSecretProtector());
        using var lease = await factory.CreateAsync(newProfile.Id, new AiChatRuntimeInputs(false, []));
        lease.Options.Temperature.Should().Be(0.2f);
        lease.Options.MaxOutputTokens.Should().Be(16000);
        AiChatOptions.ResolveContextWindowTokens(lease.Profile).Should().Be(128000);

        await restartedService.DeleteModelConfigurationAsync("model-A");
        (await restarted.ListAsync()).Should().BeEmpty();
        var unconfigured = await restarted.GetModelProfileAsync(newProfile.Id);
        unconfigured.Should().NotBeNull();
        unconfigured?.Configuration.Should().BeNull();
        unconfigured?.ModelOptions["custom.option"].GetString().Should().Be("preserved");
    }

    [Fact]
    public async Task Remote_discovery_reuses_shared_configuration_and_refresh_does_not_overwrite_it()
    {
        var repository = CreateRepository();
        var existing = await CreateProfileAsync(repository, "local-only");
        using var http = new AiProviderHttpClientProvider(() => new ModelListHandler());
        var service = new AiProviderSettingsService(repository,
            new AiProviderRegistry([new OpenAiProviderAdapter(httpClientProvider: http)]), new TestSecretProtector(), repository, http);
        var configuration = CreateConfiguration("remote-model");
        await service.SaveModelConfigurationAsync(configuration);

        var models = await service.FetchAndMergeRemoteModelsAsync(existing.ProviderConnectionId);
        var discovered = models.Single(model => model.Model == "remote-model");
        discovered.Enabled.Should().BeFalse();
        discovered.Name.Should().Be(configuration.Name);
        discovered.Configuration.Should().BeEquivalentTo(configuration);
        discovered.Sampling.Should().Be(configuration.Sampling);

        var updated = configuration with { ReasoningEffort = "max", MaxOutputTokens = 24000 };
        await service.SaveModelConfigurationAsync(updated);
        var refreshed = await service.FetchAndMergeRemoteModelsAsync(existing.ProviderConnectionId);
        refreshed.Single(model => model.Model == "remote-model").Configuration.Should().BeEquivalentTo(updated);
        refreshed.Should().Contain(model => model.Model == "local-only");
    }

    [Fact]
    public async Task Upgrading_v25_keeps_existing_profiles_options_and_default_selection()
    {
        var repository = CreateRepository();
        var profile = await CreateProfileAsync(repository, "legacy-model");
        await repository.SetModelProfileSelectionAsync(new AiModelProfileSelection("desktop.default", profile.Id, DateTimeOffset.UtcNow));
        await using (var connection = new SqliteConnection($"Data Source={Path.Combine(_root, "test.db")}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "DROP TABLE ai_model_configurations; DELETE FROM schema_versions WHERE version = 26; INSERT OR IGNORE INTO schema_versions VALUES(25, '2026-01-01');";
            await command.ExecuteNonQueryAsync();
        }

        var upgraded = CreateRepository();
        var restored = await upgraded.GetModelProfileAsync(profile.Id);
        restored.Should().BeEquivalentTo(profile, options => options.Excluding(item => item.ModelOptions));
        restored?.ModelOptions["custom.option"].GetString().Should().Be("preserved");
        (await upgraded.GetModelProfileSelectionAsync("desktop.default"))?.ModelProfileId.Should().Be(profile.Id);
        await CreateService(upgraded).SaveModelConfigurationAsync(CreateConfiguration(profile.Model));
        (await upgraded.ListAsync()).Should().ContainSingle();
    }

    [Theory]
    [InlineData("temperature")]
    [InlineData("topP")]
    [InlineData("nan")]
    [InlineData("context")]
    [InlineData("output")]
    [InlineData("budget")]
    [InlineData("price")]
    [InlineData("effort")]
    public async Task Invalid_configuration_does_not_replace_saved_values(string field)
    {
        var repository = CreateRepository();
        var service = CreateService(repository);
        var original = CreateConfiguration("model");
        await service.SaveModelConfigurationAsync(original);
        var invalid = field switch
        {
            "temperature" => original with { Sampling = original.Sampling with { Temperature = 3 } },
            "topP" => original with { Sampling = original.Sampling with { TopP = -0.1 } },
            "nan" => original with { Sampling = original.Sampling with { Temperature = double.NaN } },
            "context" => original with { ContextLength = 0 },
            "output" => original with { MaxOutputTokens = -1 },
            "budget" => original with { MaxOutputTokens = original.ContextLength },
            "price" => original with { PriceCacheReadPerMTok = -1 },
            _ => original with { ReasoningEffort = "invalid" }
        };

        var action = () => service.SaveModelConfigurationAsync(invalid);
        await action.Should().ThrowAsync<ArgumentException>();
        (await repository.ListAsync()).Single().Should().BeEquivalentTo(original);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private SqliteAiProviderRepository CreateRepository()
        => new(new SqliteDatabase(new StoragePaths(_root, Path.Combine(_root, "test.db"), Path.Combine(_root, "secrets"))));

    private static AiProviderSettingsService CreateService(SqliteAiProviderRepository repository)
        => new(repository, new AiProviderRegistry([new OpenAiProviderAdapter()]), new TestSecretProtector(), repository);

    private static AiModelConfiguration CreateConfiguration(string model)
        => new(model, "Shared model", new AiSamplingOptions(true, 0.2, false, 0.9),
            true, "high", 128000, 16000, 0.123456m, 2m, 0m, 0.25m, "Billing notes");

    private static async Task<AiModelProfile> CreateProfileAsync(SqliteAiProviderRepository repository, string model)
    {
        var now = DateTimeOffset.UtcNow;
        var connection = new AiProviderConnection(Guid.NewGuid(), "openai", "Provider", AiProviderKind.OpenAI,
            new Uri("https://api.example.test/v1/"), AiProviderAuthKind.ApiKey,
            new Dictionary<string, string> { ["api_key"] = "test" }, new Dictionary<string, JsonElement>(), now, now);
        await repository.UpsertProviderConnectionAsync(connection);
        var profile = new AiModelProfile(Guid.NewGuid(), connection.Id, model, AiProviderApiFormat.OpenAIChatCompletions,
            model, new AiSamplingOptions(false, 0.7, false, 0.7),
            new Dictionary<string, JsonElement> { ["custom.option"] = JsonSerializer.SerializeToElement("preserved") }, now, now);
        await repository.UpsertModelProfileAsync(profile);
        return profile;
    }

    private sealed class TestSecretProtector : ISecretProtector
    {
        public Task<string> StoreSecretAsync(string secret, string? existingSecretRef = null, CancellationToken cancellationToken = default) => Task.FromResult("test");
        public Task<string?> RetrieveSecretAsync(string secretRef, CancellationToken cancellationToken = default) => Task.FromResult<string?>("test-key");
        public Task DeleteSecretAsync(string secretRef, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class ModelListHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"data":[{"id":"remote-model","context_length":64000}]}""")
            });
    }
}
