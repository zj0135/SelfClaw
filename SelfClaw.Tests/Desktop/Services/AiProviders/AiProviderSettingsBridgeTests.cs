using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using SelfClaw.Desktop.Services.AiProviders;
using SelfClaw.Infrastructure.AiProviders.Abstractions;
using SelfClaw.Infrastructure.AiProviders.Models;
using SelfClaw.Infrastructure.AiProviders.Models.Views;

namespace SelfClaw.Tests.Desktop.Services.AiProviders;

public sealed class AiProviderSettingsBridgeTests
{
    public static TheoryData<string, string, string> SupportedMessages => new()
    {
        { "ai-providers/get-state", "{}", "get-state" },
        { "ai-providers/save-provider", "{\"catalogId\":\"openai\",\"name\":\"OpenAI\",\"endpoint\":\"https://api.openai.com/v1/\",\"apiKey\":\"sk-test\"}", "save-provider" },
        { "ai-providers/set-provider-enabled", ProviderBooleanPayload(), "set-provider-enabled" },
        { "ai-providers/delete-provider", ProviderPayload(), "delete-provider" },
        { "ai-providers/fetch-models", ProviderPayload(), "fetch-models" },
        { "ai-providers/check", ProviderAndModelPayload(), "check" },
        { "ai-providers/upsert-model", UpsertModelPayload(), "upsert-model" },
        { "ai-providers/set-model-enabled", ModelBooleanPayload(), "set-model-enabled" },
        { "ai-providers/set-all-models-enabled", ProviderBooleanPayload(), "set-all-models-enabled" },
        { "ai-providers/delete-model", ModelPayload(), "delete-model" },
        { "ai-providers/set-default-model", DefaultModelPayload("desktop-default"), "set-default-model" },
        { "ai-providers/list-enabled-models", "{}", "list-enabled-models" },
    };

    [Theory]
    [MemberData(nameof(SupportedMessages))]
    public async Task TryHandleAsync_routes_every_supported_message_and_echoes_request_id(
        string type,
        string payloadJson,
        string expectedCall)
    {
        var service = new RecordingSettingsService();
        var bridge = new AiProviderSettingsBridge(service);
        object? response = null;
        bridge.ResponseReady += value => response = value;
        using var document = JsonDocument.Parse(AddRequestId(payloadJson, "request-42"));

        var handled = await bridge.TryHandleAsync(type, document.RootElement);

        handled.Should().BeTrue();
        service.Calls.Should().Contain(expectedCall);
        var responseJson = SerializeResponse(response);
        responseJson.GetProperty("type").GetString().Should().Be(type);
        responseJson.GetProperty("requestId").GetString().Should().Be("request-42");
        responseJson.TryGetProperty("error", out _).Should().BeFalse();
    }

    [Fact]
    public async Task TryHandleAsync_ignores_non_provider_messages()
    {
        var service = new RecordingSettingsService();
        var bridge = new AiProviderSettingsBridge(service);
        var responseCount = 0;
        bridge.ResponseReady += _ => responseCount++;
        using var document = JsonDocument.Parse("{}");

        (await bridge.TryHandleAsync("send-prompt", document.RootElement)).Should().BeFalse();

        service.Calls.Should().BeEmpty();
        responseCount.Should().Be(0);
    }

    [Theory]
    [InlineData("ai-providers/not-supported", "{}", "Unsupported AI provider message type")]
    [InlineData("ai-providers/set-provider-enabled", "{}", "GUID property 'id' is required")]
    public async Task TryHandleAsync_returns_correlated_errors(string type, string payloadJson, string errorFragment)
    {
        var bridge = new AiProviderSettingsBridge(new RecordingSettingsService());
        object? response = null;
        bridge.ResponseReady += value => response = value;
        using var document = JsonDocument.Parse(AddRequestId(payloadJson, "bad-request"));

        (await bridge.TryHandleAsync(type, document.RootElement)).Should().BeTrue();

        var responseJson = SerializeResponse(response);
        responseJson.GetProperty("requestId").GetString().Should().Be("bad-request");
        responseJson.GetProperty("error").GetString().Should().Contain(errorFragment);
    }

    [Fact]
    public async Task List_and_desktop_default_changes_publish_the_authoritative_model_selection()
    {
        var service = new RecordingSettingsService();
        var bridge = new AiProviderSettingsBridge(service);
        var selections = new List<Guid?>();
        bridge.ModelSelectionChanged += selections.Add;

        using (var listDocument = JsonDocument.Parse("{}"))
        {
            await bridge.TryHandleAsync("ai-providers/list-enabled-models", listDocument.RootElement);
        }

        using (var defaultDocument = JsonDocument.Parse(DefaultModelPayload("desktop-default")))
        {
            await bridge.TryHandleAsync("ai-providers/set-default-model", defaultDocument.RootElement);
        }

        using (var otherScopeDocument = JsonDocument.Parse(DefaultModelPayload("other-scope")))
        {
            await bridge.TryHandleAsync("ai-providers/set-default-model", otherScopeDocument.RootElement);
        }

        selections.Should().Equal(service.DefaultModelId, RecordingSettingsService.ModelId);
    }

    [Fact]
    public async Task TryHandleAsync_propagates_caller_cancellation_without_posting_an_error()
    {
        var service = new RecordingSettingsService { ObserveCancellation = true };
        var bridge = new AiProviderSettingsBridge(service);
        var responseCount = 0;
        bridge.ResponseReady += _ => responseCount++;
        using var cancellationSource = new CancellationTokenSource();
        cancellationSource.Cancel();
        using var document = JsonDocument.Parse("{}");

        Func<Task> act = () => bridge.TryHandleAsync(
            "ai-providers/get-state",
            document.RootElement,
            cancellationSource.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        responseCount.Should().Be(0);
    }

    private static JsonElement SerializeResponse(object? response)
    {
        response.Should().NotBeNull();
        return JsonSerializer.SerializeToElement(response, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        });
    }

    private static string AddRequestId(string json, string requestId)
    {
        using var document = JsonDocument.Parse(json);
        var values = document.RootElement.EnumerateObject()
            .ToDictionary(property => property.Name, property => property.Value.Clone());
        values["requestId"] = JsonSerializer.SerializeToElement(requestId);
        return JsonSerializer.Serialize(values);
    }

    private static string ProviderPayload()
        => $"{{\"providerId\":\"{RecordingSettingsService.ProviderId:D}\"}}";

    private static string ProviderBooleanPayload()
        => $"{{\"providerId\":\"{RecordingSettingsService.ProviderId:D}\",\"enabled\":true}}";

    private static string ModelPayload()
        => $"{{\"modelProfileId\":\"{RecordingSettingsService.ModelId:D}\"}}";

    private static string ModelBooleanPayload()
        => $"{{\"modelProfileId\":\"{RecordingSettingsService.ModelId:D}\",\"enabled\":false}}";

    private static string ProviderAndModelPayload()
        => $"{{\"providerId\":\"{RecordingSettingsService.ProviderId:D}\",\"modelProfileId\":\"{RecordingSettingsService.ModelId:D}\"}}";

    private static string DefaultModelPayload(string scope)
        => $"{{\"scope\":\"{scope}\",\"modelProfileId\":\"{RecordingSettingsService.ModelId:D}\"}}";

    private static string UpsertModelPayload()
        => $"{{\"providerConnectionId\":\"{RecordingSettingsService.ProviderId:D}\",\"name\":\"GPT\",\"apiFormat\":\"OpenAIResponses\",\"model\":\"gpt-test\"}}";

    private sealed class RecordingSettingsService : IAiProviderSettingsService
    {
        public static readonly Guid ProviderId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        public static readonly Guid ModelId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        public readonly Guid DefaultModelId = Guid.Parse("33333333-3333-3333-3333-333333333333");

        public List<string> Calls { get; } = [];
        public bool ObserveCancellation { get; init; }

        public Task<AiProviderSettingsState> GetStateAsync(CancellationToken cancellationToken = default)
        {
            Record("get-state", cancellationToken);
            return Task.FromResult(new AiProviderSettingsState([], DefaultModelId));
        }

        public Task<Guid?> GetDefaultModelAsync(string scope, CancellationToken cancellationToken = default)
        {
            Record("get-default-model", cancellationToken);
            return Task.FromResult<Guid?>(DefaultModelId);
        }

        public Task<AiProviderView> SaveProviderAsync(SaveProviderCommand command, CancellationToken cancellationToken = default)
        {
            Record("save-provider", cancellationToken);
            return Task.FromResult(CreateProvider());
        }

        public Task SetProviderEnabledAsync(Guid connectionId, bool enabled, CancellationToken cancellationToken = default)
            => Complete("set-provider-enabled", cancellationToken);

        public Task DeleteProviderAsync(Guid connectionId, CancellationToken cancellationToken = default)
            => Complete("delete-provider", cancellationToken);

        public Task<IReadOnlyList<AiModelView>> FetchAndMergeRemoteModelsAsync(Guid connectionId, CancellationToken cancellationToken = default)
        {
            Record("fetch-models", cancellationToken);
            return Task.FromResult<IReadOnlyList<AiModelView>>([CreateModel()]);
        }

        public Task<ConnectivityCheckResult> CheckConnectivityAsync(Guid connectionId, Guid modelProfileId, CancellationToken cancellationToken = default)
        {
            Record("check", cancellationToken);
            return Task.FromResult(new ConnectivityCheckResult(true, 12, null));
        }

        public Task<AiModelView> UpsertModelAsync(UpsertModelCommand command, CancellationToken cancellationToken = default)
        {
            Record("upsert-model", cancellationToken);
            return Task.FromResult(CreateModel());
        }

        public Task SetModelEnabledAsync(Guid modelProfileId, bool enabled, CancellationToken cancellationToken = default)
            => Complete("set-model-enabled", cancellationToken);

        public Task SetAllModelsEnabledAsync(Guid connectionId, bool enabled, CancellationToken cancellationToken = default)
            => Complete("set-all-models-enabled", cancellationToken);

        public Task DeleteModelAsync(Guid modelProfileId, CancellationToken cancellationToken = default)
            => Complete("delete-model", cancellationToken);

        public Task SetDefaultModelAsync(string scope, Guid modelProfileId, CancellationToken cancellationToken = default)
            => Complete("set-default-model", cancellationToken);

        public Task<IReadOnlyList<EnabledModelView>> ListEnabledModelsAsync(CancellationToken cancellationToken = default)
        {
            Record("list-enabled-models", cancellationToken);
            return Task.FromResult<IReadOnlyList<EnabledModelView>>(
                [new EnabledModelView(ModelId, "GPT", "gpt-test", "OpenAI")]);
        }

        private Task Complete(string call, CancellationToken cancellationToken)
        {
            Record(call, cancellationToken);
            return Task.CompletedTask;
        }

        private void Record(string call, CancellationToken cancellationToken)
        {
            Calls.Add(call);
            if (ObserveCancellation)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
        }

        private static AiProviderView CreateProvider()
            => new(
                ProviderId,
                "openai",
                "OpenAI",
                "Official",
                "#000000",
                true,
                true,
                true,
                "sk-…test",
                "https://api.openai.com/v1/",
                AiProviderKind.OpenAI,
                AiProviderAuthKind.ApiKey,
                null,
                true,
                AiProviderApiFormat.OpenAIResponses,
                [AiProviderApiFormat.OpenAIResponses],
                new Dictionary<string, JsonElement>(),
                [CreateModel()],
                1);

        private static AiModelView CreateModel()
            => new(
                ModelId,
                ProviderId,
                "GPT",
                "gpt-test",
                AiProviderApiFormat.OpenAIResponses,
                new AiSamplingOptions(false, 0.7, false, 0.7),
                new Dictionary<string, JsonElement>(),
                true,
                null,
                null,
                null,
                null,
                null,
                null);
    }
}
