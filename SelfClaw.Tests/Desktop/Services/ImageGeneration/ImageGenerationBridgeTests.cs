using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Desktop.Services.ImageGeneration;
using SelfClaw.Infrastructure.Options;
using Xunit;

namespace SelfClaw.Tests.Desktop.Services.ImageGeneration;

public sealed class ImageGenerationBridgeTests
{
    private static readonly Guid GptProfileId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid GatewayProfileId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid ConnectionId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    [Fact]
    public async Task ListModels_downsizes_the_full_capability_contract_per_model()
    {
        var bridge = CreateBridge();

        var response = await bridge.TryHandleAsync("image-generation/list-models", EmptyPayload());

        var serialized = JsonSerializer.Serialize(response);
        using var document = JsonDocument.Parse(serialized);
        var models = document.RootElement.GetProperty("models").EnumerateArray().ToArray();
        models.Should().HaveCount(2);

        var gpt = models.Single(m => m.GetProperty("model").GetString() == "gpt-image-2.6-flare");
        var caps = gpt.GetProperty("capabilities");
        caps.GetProperty("maxPromptLength").GetInt32().Should().Be(32000);
        caps.GetProperty("supportsOutputCompression").GetBoolean().Should().BeTrue();
        caps.GetProperty("qualities").EnumerateArray().Select(x => x.GetString()).Should().Contain("xhigh");
        caps.GetProperty("backgrounds").EnumerateArray().Should().HaveCount(3);
        caps.GetProperty("outputFormats").EnumerateArray().Should().HaveCount(3);
        caps.GetProperty("moderations").EnumerateArray().Should().HaveCount(2);
        caps.GetProperty("customSize").GetProperty("multipleOf").GetInt32().Should().Be(16);

        var gateway = models.Single(m => m.GetProperty("model").GetString() == "acme/flux-pro");
        var gatewayCaps = gateway.GetProperty("capabilities");
        gatewayCaps.GetProperty("maxPromptLength").GetInt32().Should().Be(1000);
        gatewayCaps.GetProperty("customSize").ValueKind.Should().Be(JsonValueKind.Null);
        gatewayCaps.GetProperty("backgrounds").EnumerateArray().Should().BeEmpty();
    }

    [Fact]
    public async Task Generate_accepts_a_prompt_up_to_the_model_capability_limit()
    {
        var service = new CapturingImageGenerationService();
        var bridge = CreateBridge(service);
        // 4001 chars: rejected by the old hard-coded 4000 ceiling, legal for a 32000-char GPT model.
        var prompt = new string('a', 4001);

        var response = await bridge.TryHandleAsync(
            "image-generation/generate",
            GeneratePayload(GptProfileId, prompt));

        JsonSerializer.Serialize(response).Should().Contain("\"ok\":true");
        service.LastRequest.Should().NotBeNull();
        service.LastRequest!.Prompt.Length.Should().Be(4001);
    }

    [Fact]
    public async Task Generate_rejects_a_prompt_over_the_gateway_limit()
    {
        var service = new CapturingImageGenerationService();
        var bridge = CreateBridge(service);

        var response = await bridge.TryHandleAsync(
            "image-generation/generate",
            GeneratePayload(GatewayProfileId, new string('a', 1001)));

        ErrorOf(response).Should().Contain("上限 1000 字符");
        service.LastRequest.Should().BeNull();
    }

    [Fact]
    public async Task Generate_rejects_a_prompt_over_the_gpt_limit()
    {
        var service = new CapturingImageGenerationService();
        var bridge = CreateBridge(service);

        var response = await bridge.TryHandleAsync(
            "image-generation/generate",
            GeneratePayload(GptProfileId, new string('a', 32001)));

        ErrorOf(response).Should().Contain("上限 32000 字符");
        service.LastRequest.Should().BeNull();
    }

    [Fact]
    public async Task Generate_forwards_the_new_knobs_and_keeps_the_style_absent()
    {
        var service = new CapturingImageGenerationService();
        var bridge = CreateBridge(service);

        await bridge.TryHandleAsync(
            "image-generation/generate",
            GeneratePayload(GptProfileId, "a cat", extra =>
            {
                extra["background"] = "transparent";
                extra["outputFormat"] = "webp";
                extra["outputCompression"] = 85;
                extra["moderation"] = "low";
                extra["quality"] = "xhigh";
            }));

        service.LastRequest.Should().NotBeNull();
        var request = service.LastRequest!;
        request.Background.Should().Be(ImageGenerationBackground.Transparent);
        request.OutputFormat.Should().Be(ImageGenerationOutputFormat.Webp);
        request.OutputCompression.Should().Be(85);
        request.Moderation.Should().Be(ImageGenerationModeration.Low);
        request.Quality.Should().Be(ImageGenerationQuality.XHigh);
        request.Style.Should().BeNull();
    }

    private static ImageGenerationBridge CreateBridge(CapturingImageGenerationService? service = null)
    {
        var state = new AiProviderSettingsState(
            [
                new AiProviderView(
                    ConnectionId, "openai", "OpenAI", "", "", true, true, true, null, "",
                    AiProviderKind.OpenAI, AiProviderAuthKind.ApiKey, true,
                    AiProviderApiFormat.OpenAIImages,
                    [AiProviderApiFormat.OpenAIImages],
                    new Dictionary<string, JsonElement>(),
                    [
                        Model(GptProfileId, "gpt-image-2.6-flare"),
                        Model(GatewayProfileId, "acme/flux-pro")
                    ],
                    2)
            ],
            GptProfileId,
            []);

        var root = Path.Combine(Path.GetTempPath(), "SelfClawTests", Guid.NewGuid().ToString("N"));
        var paths = new StoragePaths(
            root,
            Path.Combine(root, "selfclaw.db"),
            Path.Combine(root, "secrets"),
            Path.Combine(root, "logs"),
            Path.Combine(root, "worktrees"));
        return new ImageGenerationBridge(
            service ?? new CapturingImageGenerationService(),
            new StubSettingsService(state),
            paths);
    }

    private static AiModelView Model(Guid id, string model) => new(
        id, ConnectionId, model, model, AiProviderApiFormat.OpenAIImages,
        new AiSamplingOptions(false, 0.7, false, 0.7),
        new Dictionary<string, JsonElement>(), true, null, null, null, null, null, null);

    private static string ErrorOf(object response)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(response));
        return document.RootElement.GetProperty("error").GetString() ?? string.Empty;
    }

    private static JsonElement EmptyPayload() =>
        JsonDocument.Parse("{}").RootElement.Clone();

    private static JsonElement GeneratePayload(Guid profileId, string prompt, Action<Dictionary<string, object>>? extra = null)
    {
        var values = new Dictionary<string, object>
        {
            ["prompt"] = prompt,
            ["modelProfileId"] = profileId.ToString(),
            ["count"] = 1,
            ["size"] = "1024x1024"
        };
        extra?.Invoke(values);
        return JsonSerializer.SerializeToElement(values);
    }

    private sealed class StubSettingsService(AiProviderSettingsState state) : IAiProviderSettingsService
    {
        public Task<AiProviderSettingsState> GetStateAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(state);

        public Task<IReadOnlyList<AiModelConfiguration>> ListModelConfigurationsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AiModelConfiguration> SaveModelConfigurationAsync(AiModelConfiguration configuration, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteModelConfigurationAsync(string model, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AiProviderView> SaveProviderAsync(SaveProviderCommand command, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetProviderEnabledAsync(Guid connectionId, bool enabled, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteProviderAsync(Guid connectionId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<AiModelView>> FetchAndMergeRemoteModelsAsync(Guid connectionId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ConnectivityCheckResult> CheckConnectivityAsync(Guid connectionId, Guid modelProfileId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AiModelView> UpsertModelAsync(UpsertModelCommand command, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetModelEnabledAsync(Guid modelProfileId, bool enabled, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetAllModelsEnabledAsync(Guid connectionId, bool enabled, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteModelAsync(Guid modelProfileId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetDefaultModelAsync(string scope, Guid modelProfileId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class CapturingImageGenerationService : IImageGenerationService
    {
        public ImageGenerationRequest? LastRequest { get; private set; }
        public ImageEditRequest? LastEditRequest { get; private set; }

        public Task<ImageGenerationResult> GenerateImagesAsync(Guid? modelProfileId, ImageGenerationRequest request, CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            return Task.FromResult(EmptyResult());
        }

        public Task<ImageGenerationResult> GenerateImageEditsAsync(Guid? modelProfileId, ImageEditRequest request, CancellationToken cancellationToken = default)
        {
            LastEditRequest = request;
            return Task.FromResult(EmptyResult());
        }

        public ImageGenerationModelCapabilities GetModelCapabilities(string model) =>
            model.Contains("gpt-image", StringComparison.OrdinalIgnoreCase) ? GptCapabilities() : GatewayCapabilities();

        public Task<ImageGenerationModelCapabilities> GetCapabilitiesForProfileAsync(Guid? modelProfileId, CancellationToken cancellationToken = default)
            => Task.FromResult(modelProfileId == GatewayProfileId ? GatewayCapabilities() : GptCapabilities());

        private static ImageGenerationResult EmptyResult() => new([], TimeSpan.Zero, null);

        private static ImageGenerationModelCapabilities GptCapabilities() => new(
            ["auto", "1024x1024", "1536x1024", "1024x1536"],
            [ImageGenerationQuality.Auto, ImageGenerationQuality.XHigh],
            [],
            10,
            32000,
            [ImageGenerationBackground.Auto, ImageGenerationBackground.Transparent, ImageGenerationBackground.Opaque],
            [ImageGenerationOutputFormat.Png, ImageGenerationOutputFormat.Jpeg, ImageGenerationOutputFormat.Webp],
            [ImageGenerationModeration.Auto, ImageGenerationModeration.Low],
            true,
            ImageGenerationCustomSizeRules.GptImage);

        private static ImageGenerationModelCapabilities GatewayCapabilities() => new(
            ["1024x1024"],
            [ImageGenerationQuality.Standard],
            [ImageGenerationStyle.Vivid, ImageGenerationStyle.Natural],
            10,
            ImageGenerationModelCapabilities.DefaultMaxPromptLength);
    }
}
