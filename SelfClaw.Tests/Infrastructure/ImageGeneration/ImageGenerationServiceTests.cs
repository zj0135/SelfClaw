using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Infrastructure.AiProviders;
using SelfClaw.Infrastructure.AiProviders.Abstractions;
using SelfClaw.Infrastructure.AiProviders.Http;
using SelfClaw.Infrastructure.AiProviders.Models;
using SelfClaw.Infrastructure.ImageGeneration;
using Xunit;

namespace SelfClaw.Tests.Infrastructure.ImageGeneration;

public sealed class ImageGenerationServiceTests
{
    [Fact]
    public async Task GenerateImagesAsync_throws_when_request_is_null()
    {
        var service = CreateService(new FakeAiProviderRepository(), new FakeSecretProtector(), new FakeAdapter());

        var act = () => service.GenerateImagesAsync(Guid.NewGuid(), null!);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task GenerateImagesAsync_throws_when_model_profile_not_found()
    {
        var repo = new FakeAiProviderRepository();
        var service = CreateService(repo, new FakeSecretProtector(), new FakeAdapter());
        var request = new ImageGenerationRequest("a cat");

        var act = () => service.GenerateImagesAsync(Guid.NewGuid(), request);

        await act.Should().ThrowAsync<KeyNotFoundException>();
    }

    [Fact]
    public async Task GenerateImagesAsync_throws_when_model_profile_is_disabled()
    {
        var data = CreateData(profileEnabled: false);
        var service = CreateService(data.Repository, new FakeSecretProtector(), new FakeAdapter());
        var request = new ImageGenerationRequest("a cat");

        var act = () => service.GenerateImagesAsync(data.Profile.Id, request);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage($"AI model profile '{data.Profile.Name}' is disabled.");
    }

    [Fact]
    public async Task GenerateImagesAsync_throws_when_profile_format_is_not_OpenAIImages()
    {
        var data = CreateData(apiFormat: AiProviderApiFormat.OpenAIChatCompletions);
        var service = CreateService(data.Repository, new FakeSecretProtector(), new FakeAdapter());
        var request = new ImageGenerationRequest("a cat");

        var act = () => service.GenerateImagesAsync(data.Profile.Id, request);

        await act.Should().ThrowAsync<NotSupportedException>()
            .WithMessage($"*OpenAIImages*");
    }

    [Fact]
    public async Task GenerateImagesAsync_throws_when_connection_not_found()
    {
        var profile = CreateProfile(Guid.NewGuid(), Guid.NewGuid(), AiProviderApiFormat.OpenAIImages, true);
        var repo = new FakeAiProviderRepository { Profiles = { [profile.Id] = profile } };
        var service = CreateService(repo, new FakeSecretProtector(), new FakeAdapter());
        var request = new ImageGenerationRequest("a cat");

        var act = () => service.GenerateImagesAsync(profile.Id, request);

        await act.Should().ThrowAsync<KeyNotFoundException>()
            .WithMessage($"*{profile.ProviderConnectionId}*");
    }

    [Fact]
    public async Task GenerateImagesAsync_throws_when_connection_is_disabled()
    {
        var data = CreateData(connectionEnabled: false);
        var service = CreateService(data.Repository, new FakeSecretProtector(), new FakeAdapter());
        var request = new ImageGenerationRequest("a cat");

        var act = () => service.GenerateImagesAsync(data.Profile.Id, request);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage($"AI provider connection '{data.Connection.Name}' is disabled.");
    }

    [Fact]
    public async Task GenerateImagesAsync_throws_when_adapter_does_not_support_format()
    {
        var data = CreateData();
        var adapter = new FakeAdapter { SupportsFormat = false };
        var service = CreateService(data.Repository, new FakeSecretProtector(), adapter);
        var request = new ImageGenerationRequest("a cat");

        var act = () => service.GenerateImagesAsync(data.Profile.Id, request);

        await act.Should().ThrowAsync<NotSupportedException>()
            .WithMessage($"*{data.Profile.ApiFormat}*");
    }

    [Fact]
    public async Task GenerateImagesAsync_throws_when_no_enabled_profile_exists_and_id_is_null()
    {
        var repo = new FakeAiProviderRepository();
        var service = CreateService(repo, new FakeSecretProtector(), new FakeAdapter());
        var request = new ImageGenerationRequest("a cat");

        var act = () => service.GenerateImagesAsync(null, request);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*No enabled image generation model profile found*");
    }

    [Fact]
    public void OpenAiProviderAdapter_SupportsApiFormat_includes_OpenAIImages()
    {
        var adapter = new SelfClaw.Infrastructure.AiProviders.OpenAi.OpenAiProviderAdapter(AiProviderKind.OpenAI);

        adapter.SupportsApiFormat(AiProviderApiFormat.OpenAIChatCompletions).Should().BeTrue();
        adapter.SupportsApiFormat(AiProviderApiFormat.OpenAIResponses).Should().BeTrue();
        adapter.SupportsApiFormat(AiProviderApiFormat.OpenAIImages).Should().BeTrue();
        adapter.SupportsApiFormat(AiProviderApiFormat.AnthropicMessages).Should().BeFalse();
    }

    [Fact]
    public void OpenAiImageGenerationClient_throws_when_initialized_with_null()
    {
        var act = () => new OpenAiImageGenerationClient(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    private static (AiProviderConnection Connection, AiModelProfile Profile, FakeAiProviderRepository Repository) CreateData(
        bool profileEnabled = true,
        bool connectionEnabled = true,
        AiProviderApiFormat apiFormat = AiProviderApiFormat.OpenAIImages)
    {
        var now = DateTimeOffset.UtcNow;
        var connectionId = Guid.NewGuid();
        var profileId = Guid.NewGuid();
        var connection = new AiProviderConnection(
            connectionId,
            "custom",
            "Test OpenAI",
            AiProviderKind.OpenAI,
            new Uri("https://api.openai.com/v1"),
            AiProviderAuthKind.ApiKey,
            new Dictionary<string, string> { ["api_key"] = "secret:test-key" },
            new Dictionary<string, JsonElement>(),
            now,
            now,
            connectionEnabled);

        var profile = CreateProfile(profileId, connectionId, apiFormat, profileEnabled);

        var repository = new FakeAiProviderRepository
        {
            Connections = { [connectionId] = connection },
            Profiles = { [profileId] = profile }
        };

        return (connection, profile, repository);
    }

    private static AiModelProfile CreateProfile(
        Guid profileId,
        Guid connectionId,
        AiProviderApiFormat apiFormat,
        bool enabled)
    {
        var now = DateTimeOffset.UtcNow;
        return new AiModelProfile(
            profileId,
            connectionId,
            "dall-e-3",
            apiFormat,
            "dall-e-3",
            new AiSamplingOptions(false, 0.7, false, 0.7),
            new Dictionary<string, JsonElement>(),
            now,
            now,
            enabled);
    }

    private static ImageGenerationService CreateService(
        IAiProviderRepository repository,
        ISecretProtector protector,
        IAiProviderAdapter adapter)
    {
        var registry = new AiProviderRegistry([adapter]);
        var httpClientProvider = new AiProviderHttpClientProvider(_ => new StubHandler());
        return new ImageGenerationService(
            repository,
            registry,
            protector,
            httpClientProvider,
            NullLogger<ImageGenerationService>.Instance);
    }

    private sealed class FakeSecretProtector : ISecretProtector
    {
        public Dictionary<string, string> Secrets { get; } = new();

        public Task<string> StoreSecretAsync(string secret, string? existingSecretRef = null, CancellationToken cancellationToken = default)
        {
            var r = existingSecretRef ?? $"secret:{Guid.NewGuid():N}";
            Secrets[r] = secret;
            return Task.FromResult(r);
        }

        public Task<string?> RetrieveSecretAsync(string secretRef, CancellationToken cancellationToken = default)
            => Task.FromResult<string?>(Secrets.TryGetValue(secretRef, out var val) ? val : "dummy-decrypted-key");

        public Task DeleteSecretAsync(string secretRef, CancellationToken cancellationToken = default)
        {
            Secrets.Remove(secretRef);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeAdapter : IAiProviderAdapter
    {
        public AiProviderKind ProviderKind => AiProviderKind.OpenAI;
        public bool SupportsModelListing => false;
        public bool SupportsFormat { get; init; } = true;

        public bool SupportsApiFormat(AiProviderApiFormat apiFormat) => SupportsFormat;

        public Microsoft.Extensions.AI.IChatClient CreateChatClient(AiProviderClientRequest request, HttpClient httpClient)
            => throw new NotSupportedException();

        public Microsoft.Extensions.AI.ChatOptions CreateChatOptions(AiProviderClientRequest request, IReadOnlyList<Microsoft.Extensions.AI.AITool> tools)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<AiModelDescriptor>> ListModelsAsync(
            AiProviderConnection connection,
            IReadOnlyDictionary<string, string> secrets,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
    }

    private sealed class FakeAiProviderRepository : IAiProviderRepository
    {
        public Dictionary<Guid, AiProviderConnection> Connections { get; } = new();
        public Dictionary<Guid, AiModelProfile> Profiles { get; } = new();

        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<AiProviderConnection>> ListProviderConnectionsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<AiProviderConnection>>(Connections.Values.ToList());

        public Task<IReadOnlyList<AiProviderConnection>> ListAllProviderConnectionsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<AiProviderConnection>>(Connections.Values.ToList());

        public Task<AiProviderConnection?> GetProviderConnectionAsync(Guid providerConnectionId, CancellationToken cancellationToken = default)
            => Task.FromResult(Connections.GetValueOrDefault(providerConnectionId));

        public Task<AiProviderConnection> UpsertProviderConnectionAsync(AiProviderConnection connection, CancellationToken cancellationToken = default)
        {
            Connections[connection.Id] = connection;
            return Task.FromResult(connection);
        }

        public Task SetProviderConnectionEnabledAsync(Guid providerConnectionId, bool isEnabled, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task DeleteProviderConnectionAsync(Guid providerConnectionId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<AiModelProfile>> ListModelProfilesAsync(Guid? providerConnectionId = null, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<AiModelProfile>>(Profiles.Values.ToList());

        public Task<AiModelProfile?> GetModelProfileAsync(Guid modelProfileId, CancellationToken cancellationToken = default)
            => Task.FromResult(Profiles.GetValueOrDefault(modelProfileId));

        public Task<AiModelProfile> UpsertModelProfileAsync(AiModelProfile profile, CancellationToken cancellationToken = default)
        {
            Profiles[profile.Id] = profile;
            return Task.FromResult(profile);
        }

        public Task SetModelProfileEnabledAsync(Guid modelProfileId, bool isEnabled, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task SetAllModelProfilesEnabledAsync(Guid providerConnectionId, bool isEnabled, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<AiModelProfile>> ListEnabledModelProfilesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<AiModelProfile>>(Profiles.Values.Where(p => p.IsEnabled).ToList());

        public Task DeleteModelProfileAsync(Guid modelProfileId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<AiModelProfileSelection?> GetModelProfileSelectionAsync(string scope, CancellationToken cancellationToken = default)
            => Task.FromResult<AiModelProfileSelection?>(null);

        public Task<AiModelProfileSelection> SetModelProfileSelectionAsync(AiModelProfileSelection selection, CancellationToken cancellationToken = default)
            => Task.FromResult(selection);
    }
}
