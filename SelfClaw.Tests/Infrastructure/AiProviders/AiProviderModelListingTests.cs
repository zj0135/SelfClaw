using System.Net;
using System.Net.Http.Headers;
using System.Text;
using FluentAssertions;
using SelfClaw.Infrastructure.AiProviders.Anthropic;
using SelfClaw.Infrastructure.AiProviders.Models;
using SelfClaw.Infrastructure.AiProviders.OpenAi;

namespace SelfClaw.Tests.Infrastructure.AiProviders;

public sealed class AiProviderModelListingTests
{
    [Theory]
    [InlineData(AiProviderKind.OpenAI, "openai-models.json", "gpt-4.1,gpt-4o-mini")]
    [InlineData(AiProviderKind.DeepSeek, "deepseek-models.json", "deepseek-chat,deepseek-reasoner")]
    public async Task OpenAi_family_adapter_lists_models_with_bearer_authentication(
        AiProviderKind providerKind,
        string fixtureName,
        string expectedModelIds)
    {
        var handler = new RecordingHttpMessageHandler(ReadFixture(fixtureName));
        var adapter = new OpenAiProviderAdapter(
            providerKind,
            modelListClient: new OpenAiModelListClient(new HttpClient(handler)));
        var connection = CreateConnection(
            providerKind == AiProviderKind.OpenAI ? "openai" : "deepseek",
            providerKind,
            new Uri("https://api.example.test/v1/"));

        var models = await adapter.ListModelsAsync(
            connection,
            new Dictionary<string, string> { [OpenAiProviderAdapter.ApiKeySecretName] = "test-key" });

        adapter.SupportsModelListing.Should().BeTrue();
        models.Select(model => model.ModelId).Should().Equal(expectedModelIds.Split(','));
        foreach (var model in models)
        {
            model.DisplayName.Should().BeNull();
            model.ContextLength.Should().BeNull();
            model.MaxOutputTokens.Should().BeNull();
        }
        handler.Requests.Should().ContainSingle();
        handler.Requests[0].Uri.Should().Be(new Uri("https://api.example.test/v1/models"));
        handler.Requests[0].AuthorizationScheme.Should().Be("Bearer");
        handler.Requests[0].AuthorizationParameter.Should().Be("test-key");
    }

    [Fact]
    public async Task Anthropic_adapter_lists_all_pages_and_maps_display_metadata()
    {
        var handler = new RecordingHttpMessageHandler(
            ReadFixture("anthropic-models-page-1.json"),
            ReadFixture("anthropic-models-page-2.json"));
        var adapter = new AnthropicProviderAdapter(
            modelListClient: new AnthropicModelListClient(new HttpClient(handler)));
        var connection = CreateConnection(
            "anthropic",
            AiProviderKind.Anthropic,
            new Uri("https://api.anthropic.test/"));

        var models = await adapter.ListModelsAsync(
            connection,
            new Dictionary<string, string> { [AnthropicProviderAdapter.ApiKeySecretName] = "anthropic-key" });

        adapter.SupportsModelListing.Should().BeTrue();
        models.Should().HaveCount(2);
        models[0].Should().Be(new AiModelDescriptor(
            "claude-sonnet-4-5",
            "Claude Sonnet 4.5",
            200000,
            64000,
            null,
            null,
            null,
            null));
        models[1].ModelId.Should().Be("claude-haiku-4-5");
        models[1].ContextLength.Should().Be(200000);
        models[1].MaxOutputTokens.Should().Be(32000);

        handler.Requests.Should().HaveCount(2);
        handler.Requests[0].Uri.PathAndQuery.Should().Be("/v1/models?limit=100");
        handler.Requests[1].Uri.PathAndQuery.Should()
            .Be("/v1/models?limit=100&after_id=claude-sonnet-4-5");
        foreach (var request in handler.Requests)
        {
            request.Headers["x-api-key"].Should().Equal("anthropic-key");
            request.Headers["anthropic-version"].Should().Equal("2023-06-01");
        }
    }

    [Fact]
    public async Task OpenRouter_model_list_maps_context_and_per_token_pricing_to_per_million()
    {
        var handler = new RecordingHttpMessageHandler(ReadFixture("openrouter-models.json"));
        var adapter = new OpenAiProviderAdapter(
            AiProviderKind.OpenAICompatible,
            modelListClient: new OpenAiModelListClient(new HttpClient(handler)));
        var connection = CreateConnection(
            "openrouter",
            AiProviderKind.OpenAICompatible,
            new Uri("https://openrouter.ai/api/v1/"));

        var models = await adapter.ListModelsAsync(
            connection,
            new Dictionary<string, string> { [OpenAiProviderAdapter.ApiKeySecretName] = "openrouter-key" });

        models.Should().HaveCount(2);
        models[0].Should().Be(new AiModelDescriptor(
            "anthropic/claude-sonnet-4.5",
            "Anthropic: Claude Sonnet 4.5",
            1000000,
            null,
            3m,
            15m,
            3.75m,
            0.3m));
        models[1].ModelId.Should().Be("openai/gpt-4o-mini");
        models[1].PriceInPerMTok.Should().Be(0.15m);
        models[1].PriceOutPerMTok.Should().Be(0.6m);
        models[1].PriceCacheWritePerMTok.Should().BeNull();
        models[1].PriceCacheReadPerMTok.Should().BeNull();
        handler.Requests.Should().ContainSingle();
        handler.Requests[0].Uri.Should().Be(new Uri("https://openrouter.ai/api/v1/models"));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("not-json")]
    public async Task Custom_gateway_model_list_failure_explains_models_contract(string response)
    {
        var handler = new RecordingHttpMessageHandler(response);
        var adapter = new OpenAiProviderAdapter(
            AiProviderKind.OpenAICompatible,
            modelListClient: new OpenAiModelListClient(new HttpClient(handler)));
        var connection = CreateConnection(
            "custom",
            AiProviderKind.OpenAICompatible,
            new Uri("https://gateway.example.test/v1/"));

        var act = () => adapter.ListModelsAsync(
            connection,
            new Dictionary<string, string> { [OpenAiProviderAdapter.ApiKeySecretName] = "gateway-key" });

        await act.Should().ThrowAsync<InvalidDataException>()
            .WithMessage("*does not implement '/models'*non-OpenAI*");
    }

    [Fact]
    public async Task Model_listing_rejects_missing_api_key_before_sending_request()
    {
        var handler = new RecordingHttpMessageHandler(ReadFixture("openai-models.json"));
        var adapter = new OpenAiProviderAdapter(
            modelListClient: new OpenAiModelListClient(new HttpClient(handler)));
        var connection = CreateConnection("openai", AiProviderKind.OpenAI, new Uri("https://api.example.test/v1/"));

        var act = () => adapter.ListModelsAsync(connection, new Dictionary<string, string>());

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*api_key*");
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Model_listing_surfaces_the_response_body_on_a_non_success_status()
    {
        var handler = new StatusHttpMessageHandler(
            HttpStatusCode.Unauthorized,
            "{\"error\":{\"message\":\"Invalid API key provided.\"}}");
        var adapter = new OpenAiProviderAdapter(
            modelListClient: new OpenAiModelListClient(new HttpClient(handler)));
        var connection = CreateConnection("openai", AiProviderKind.OpenAI, new Uri("https://api.example.test/v1/"));

        var act = () => adapter.ListModelsAsync(
            connection,
            new Dictionary<string, string> { [OpenAiProviderAdapter.ApiKeySecretName] = "bad-key" });

        (await act.Should().ThrowAsync<HttpRequestException>())
            .Which.Message.Should().Contain("401").And.Contain("Invalid API key provided.");
    }

    private static AiProviderConnection CreateConnection(
        string catalogId,
        AiProviderKind providerKind,
        Uri endpoint)
    {
        var now = DateTimeOffset.UtcNow;
        return new AiProviderConnection(
            Guid.NewGuid(),
            catalogId,
            catalogId,
            providerKind,
            endpoint,
            AiProviderAuthKind.ApiKey,
            new Dictionary<string, string> { ["api_key"] = "secret:test" },
            new Dictionary<string, System.Text.Json.JsonElement>(),
            now,
            now);
    }

    private static string ReadFixture(string fixtureName)
        => File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "Infrastructure",
            "AiProviders",
            "Fixtures",
            fixtureName));

    private sealed class RecordingHttpMessageHandler : HttpMessageHandler
    {
        private readonly Queue<string> _responses;

        public RecordingHttpMessageHandler(params string[] responses)
        {
            _responses = new Queue<string>(responses);
        }

        public List<CapturedRequest> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (_responses.Count == 0)
            {
                throw new InvalidOperationException("No fake HTTP response remains.");
            }

            Requests.Add(new CapturedRequest(
                request.RequestUri!,
                request.Headers.Authorization?.Scheme,
                request.Headers.Authorization?.Parameter,
                request.Headers.ToDictionary(
                    header => header.Key,
                    header => header.Value.ToArray(),
                    StringComparer.OrdinalIgnoreCase)));

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_responses.Dequeue(), Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed record CapturedRequest(
        Uri Uri,
        string? AuthorizationScheme,
        string? AuthorizationParameter,
        IReadOnlyDictionary<string, string[]> Headers);

    private sealed class StatusHttpMessageHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _statusCode;
        private readonly string _body;

        public StatusHttpMessageHandler(HttpStatusCode statusCode, string body)
        {
            _statusCode = statusCode;
            _body = body;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(_statusCode)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json")
            });
    }
}
