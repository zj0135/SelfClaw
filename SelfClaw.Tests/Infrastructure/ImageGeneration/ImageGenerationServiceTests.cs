using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
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
    private const string OnePixelPng =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==";

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
        var service = CreateService(new FakeAiProviderRepository(), new FakeSecretProtector(), new FakeAdapter());

        var act = () => service.GenerateImagesAsync(Guid.NewGuid(), new ImageGenerationRequest("a cat"));

        await act.Should().ThrowAsync<KeyNotFoundException>();
    }

    [Fact]
    public async Task GenerateImagesAsync_throws_when_model_profile_is_disabled()
    {
        var data = CreateData(profileEnabled: false);
        var service = CreateService(data.Repository, new FakeSecretProtector(), new FakeAdapter());

        var act = () => service.GenerateImagesAsync(data.Profile.Id, new ImageGenerationRequest("a cat"));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage($"AI model profile '{data.Profile.Name}' is disabled.");
    }

    [Fact]
    public async Task GenerateImagesAsync_throws_when_profile_format_is_not_OpenAIImages()
    {
        var data = CreateData(apiFormat: AiProviderApiFormat.OpenAIChatCompletions);
        var service = CreateService(data.Repository, new FakeSecretProtector(), new FakeAdapter());

        var act = () => service.GenerateImagesAsync(data.Profile.Id, new ImageGenerationRequest("a cat"));

        await act.Should().ThrowAsync<NotSupportedException>()
            .WithMessage("*OpenAIImages*");
    }

    [Fact]
    public async Task GenerateImagesAsync_throws_when_connection_not_found()
    {
        var profile = CreateProfile(Guid.NewGuid(), Guid.NewGuid(), AiProviderApiFormat.OpenAIImages, true);
        var repo = new FakeAiProviderRepository { Profiles = { [profile.Id] = profile } };
        var service = CreateService(repo, new FakeSecretProtector(), new FakeAdapter());

        var act = () => service.GenerateImagesAsync(profile.Id, new ImageGenerationRequest("a cat"));

        await act.Should().ThrowAsync<KeyNotFoundException>()
            .WithMessage($"*{profile.ProviderConnectionId}*");
    }

    [Fact]
    public async Task GenerateImagesAsync_throws_when_connection_is_disabled()
    {
        var data = CreateData(connectionEnabled: false);
        var service = CreateService(data.Repository, new FakeSecretProtector(), new FakeAdapter());

        var act = () => service.GenerateImagesAsync(data.Profile.Id, new ImageGenerationRequest("a cat"));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage($"AI provider connection '{data.Connection.Name}' is disabled.");
    }

    [Fact]
    public async Task GenerateImagesAsync_throws_when_adapter_does_not_support_format()
    {
        var data = CreateData();
        var adapter = new FakeAdapter { SupportsFormat = false };
        var service = CreateService(data.Repository, new FakeSecretProtector(), adapter);

        var act = () => service.GenerateImagesAsync(data.Profile.Id, new ImageGenerationRequest("a cat"));

        await act.Should().ThrowAsync<NotSupportedException>()
            .WithMessage($"*{data.Profile.ApiFormat}*");
    }

    [Fact]
    public async Task GenerateImagesAsync_throws_when_no_enabled_profile_exists_and_id_is_null()
    {
        var service = CreateService(new FakeAiProviderRepository(), new FakeSecretProtector(), new FakeAdapter());

        var act = () => service.GenerateImagesAsync(null, new ImageGenerationRequest("a cat"));

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*No enabled image generation model profile found*");
    }

    [Fact]
    public void GetModelCapabilities_limits_dall_e_3_to_one_image()
    {
        var capabilities = OpenAiImageModelCapabilities.Resolve("dall-e-3");

        capabilities.MaxCount.Should().Be(1);
        capabilities.Sizes.Should().Equal("1024x1024", "1024x1792", "1792x1024");
        capabilities.Qualities.Should().BeEquivalentTo([ImageGenerationQuality.Standard, ImageGenerationQuality.Hd]);
        capabilities.Styles.Should().NotBeEmpty();
    }

    [Fact]
    public void GetModelCapabilities_gives_gpt_image_1_its_own_size_and_quality_set()
    {
        var capabilities = OpenAiImageModelCapabilities.Resolve("gpt-image-1");

        capabilities.MaxCount.Should().Be(10);
        capabilities.Sizes.Should().Equal("1024x1024", "1024x1536", "1536x1024");
        capabilities.Sizes.Should().NotContain("1792x1024");
        capabilities.Qualities.Should().BeEquivalentTo(
        [
            ImageGenerationQuality.Auto,
            ImageGenerationQuality.Low,
            ImageGenerationQuality.Medium,
            ImageGenerationQuality.High
        ]);
        capabilities.Styles.Should().BeEmpty();
    }

    [Fact]
    public void GetModelCapabilities_stays_permissive_for_unknown_gateway_models()
    {
        var capabilities = OpenAiImageModelCapabilities.Resolve("acme/flux-pro");

        capabilities.Sizes.Should().Contain("1792x1024");
        capabilities.Qualities.Should().Contain(ImageGenerationQuality.Standard);
        capabilities.Qualities.Should().Contain(ImageGenerationQuality.High);
        capabilities.MaxCount.Should().Be(10);
    }

    [Fact]
    public async Task Client_omits_response_format_and_style_for_gpt_image_1()
    {
        // Regression guard for the P0 defect: gpt-image-1 answers 400 to both fields, so the request
        // must not carry them even though dall-e-3 needs response_format.
        var handler = new RecordingHandler();
        var client = CreateClient(handler, "gpt-image-1");

        await client.GenerateImagesAsync(new ImageGenerationRequest(
            "a cat",
            Count: 2,
            Size: "1024x1536",
            Quality: ImageGenerationQuality.High));

        using var body = JsonDocument.Parse(handler.Body!);
        var root = body.RootElement;
        root.GetProperty("model").GetString().Should().Be("gpt-image-1");
        root.GetProperty("n").GetInt32().Should().Be(2);
        root.GetProperty("size").GetString().Should().Be("1024x1536");
        root.GetProperty("quality").GetString().Should().Be("high");
        root.TryGetProperty("response_format", out _).Should().BeFalse();
        root.TryGetProperty("style", out _).Should().BeFalse();
    }

    [Fact]
    public async Task Client_sends_dall_e_3_style_and_hd_quality()
    {
        var handler = new RecordingHandler();
        var client = CreateClient(handler, "dall-e-3");

        await client.GenerateImagesAsync(new ImageGenerationRequest(
            "a cat",
            Size: "1792x1024",
            Quality: ImageGenerationQuality.Hd,
            Style: ImageGenerationStyle.Natural));

        using var body = JsonDocument.Parse(handler.Body!);
        var root = body.RootElement;
        root.GetProperty("quality").GetString().Should().Be("hd");
        root.GetProperty("style").GetString().Should().Be("natural");
        root.GetProperty("size").GetString().Should().Be("1792x1024");
        root.GetProperty("n").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task Client_refuses_a_batch_larger_than_the_model_allows()
    {
        var client = CreateClient(new RecordingHandler(), "dall-e-3");

        var act = () => client.GenerateImagesAsync(new ImageGenerationRequest("a cat", Count: 4));

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>()
            .WithMessage("*between 1 and 1*");
    }

    [Fact]
    public async Task Client_refuses_a_size_the_model_does_not_support()
    {
        var client = CreateClient(new RecordingHandler(), "gpt-image-1");

        var act = () => client.GenerateImagesAsync(new ImageGenerationRequest("a cat", Size: "1792x1024"));

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*does not support size '1792x1024'*1024x1536*");
    }

    [Fact]
    public async Task Client_maps_bytes_revised_prompt_and_duration()
    {
        var handler = new RecordingHandler();
        var profileId = Guid.NewGuid();
        var client = CreateClient(handler, "dall-e-3", profileId);

        var result = await client.GenerateImagesAsync(new ImageGenerationRequest("a cat"));

        result.Images.Should().ContainSingle();
        result.Images[0].ImageBytes.Should().NotBeNull();
        Convert.ToBase64String(result.Images[0].ImageBytes!.Value.Span).Should().Be(OnePixelPng);
        result.Images[0].RevisedPrompt.Should().Be("a photorealistic cat");
        result.ModelProfileId.Should().Be(profileId);
        result.Duration.Should().BeGreaterThan(TimeSpan.Zero);
    }

    [Fact]
    public async Task Client_sends_the_source_image_as_multipart()
    {
        var handler = new RecordingHandler();
        var client = CreateClient(handler, "gpt-image-1");

        await client.GenerateImageEditsAsync(new ImageEditRequest(
            Convert.FromBase64String(OnePixelPng),
            "make it red",
            Size: "1024x1024"));

        handler.Url.Should().EndWith("/images/edits");
        handler.Body.Should().Contain("name=image");
        handler.Body.Should().Contain("name=prompt");
        handler.Body.Should().Contain("make it red");
        handler.Body.Should().Contain("1024x1024");
        handler.Body.Should().NotContain("response_format");
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

    [Theory]
    [InlineData(AiProviderApiFormat.OpenAIChatCompletions, true)]
    [InlineData(AiProviderApiFormat.OpenAIResponses, true)]
    [InlineData(AiProviderApiFormat.AnthropicMessages, true)]
    [InlineData(AiProviderApiFormat.OpenAIImages, false)]
    public void Only_chat_formats_count_as_chat_capable(AiProviderApiFormat format, bool expected)
        => format.IsChatFormat().Should().Be(expected);

    private static OpenAiImageGenerationClient CreateClient(
        RecordingHandler handler,
        string model,
        Guid? modelProfileId = null)
    {
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.openai.com/v1") };
        var nativeClient = new OpenAI.Images.ImageClient(
            model,
            new System.ClientModel.ApiKeyCredential("test-key"),
            new OpenAI.OpenAIClientOptions
            {
                Endpoint = new Uri("https://api.openai.com/v1"),
                Transport = new System.ClientModel.Primitives.HttpClientPipelineTransport(httpClient)
            });

        return new OpenAiImageGenerationClient(
            nativeClient,
            model,
            OpenAiImageModelCapabilities.Resolve(model),
            modelProfileId);
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
        return new ImageGenerationService(repository, registry, protector, httpClientProvider);
    }

    /// <summary>Records the outbound request and answers with one rendered image.</summary>
    private sealed class RecordingHandler : HttpMessageHandler
    {
        public string? Url { get; private set; }

        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Url = request.RequestUri?.ToString();
            if (request.Content is not null)
            {
                var bytes = await request.Content.ReadAsByteArrayAsync(cancellationToken);
                // Multipart bodies carry raw image bytes; latin1 keeps them printable for assertions.
                Body = Encoding.Latin1.GetString(bytes);
            }

            var payload = $$"""
                {"created":1,"data":[{"b64_json":"{{OnePixelPng}}","revised_prompt":"a photorealistic cat"}]}
                """;
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };
        }
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
