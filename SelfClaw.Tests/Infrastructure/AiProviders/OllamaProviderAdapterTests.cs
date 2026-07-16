using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using SelfClaw.Infrastructure.AiProviders.Http;
using SelfClaw.Infrastructure.AiProviders.Models;
using SelfClaw.Infrastructure.AiProviders.Ollama;

namespace SelfClaw.Tests.Infrastructure.AiProviders;

public sealed class OllamaProviderAdapterTests
{
    [Fact]
    public void Adapter_supports_native_and_openai_compatibility_without_an_api_key()
    {
        using var httpClientProvider = new AiProviderHttpClientProvider(() => new FixtureHandler("{}"));
        var adapter = new OllamaProviderAdapter(httpClientProvider);
        var connection = CreateConnection();
        var nativeRequest = CreateRequest(connection, AiProviderApiFormat.OllamaNative);
        var compatibleRequest = CreateRequest(connection, AiProviderApiFormat.OpenAIChatCompletions);

        adapter.ProviderKind.Should().Be(AiProviderKind.Ollama);
        adapter.SupportsModelListing.Should().BeTrue();
        adapter.SupportsApiFormat(AiProviderApiFormat.OllamaNative).Should().BeTrue();
        adapter.SupportsApiFormat(AiProviderApiFormat.OpenAIChatCompletions).Should().BeTrue();
        adapter.SupportsApiFormat(AiProviderApiFormat.OpenAIResponses).Should().BeFalse();

        using var nativeClient = adapter.CreateChatClient(nativeRequest);
        using var compatibleClient = adapter.CreateChatClient(compatibleRequest);
        nativeClient.Should().NotBeNull();
        compatibleClient.Should().NotBeNull();
        httpClientProvider.CachedClientCount.Should().Be(2);

        var options = adapter.CreateChatOptions(nativeRequest);
        options.ModelId.Should().Be("llama3.2:latest");
        options.Temperature.Should().BeApproximately(0.25f, 0.0001f);
        options.TopP.Should().BeNull();
    }

    [Fact]
    public async Task ListModelsAsync_parses_api_tags_without_authentication()
    {
        var requests = new List<CapturedRequest>();
        var fixture = ReadFixture("ollama-tags.json");
        using var httpClientProvider = new AiProviderHttpClientProvider(() => new FixtureHandler(fixture, requests));
        var adapter = new OllamaProviderAdapter(httpClientProvider);
        var connection = CreateConnection() with
        {
            ConnectionOptions = JsonObject("{\"extra_headers\":{\"X-Title\":\"SelfClaw\"}}")
        };

        var models = await adapter.ListModelsAsync(connection, new Dictionary<string, string>());

        models.Select(model => model.ModelId).Should().Equal("llama3.2:latest", "qwen2.5-coder:7b");
        models.Select(model => model.DisplayName).Should().Equal("llama3.2:latest", "qwen2.5-coder:7b");
        requests.Should().ContainSingle();
        requests[0].Uri.Should().Be(new Uri("http://localhost:11434/api/tags"));
        requests[0].Authorization.Should().BeNull();
        requests[0].Headers["X-Title"].Should().Equal("SelfClaw");
        httpClientProvider.GetNonStreamingClient(connection).Timeout.Should().Be(TimeSpan.FromSeconds(100));
    }

    [Fact]
    public void Unsupported_format_throws_a_readable_error()
    {
        using var httpClientProvider = new AiProviderHttpClientProvider(() => new FixtureHandler("{}"));
        var adapter = new OllamaProviderAdapter(httpClientProvider);
        var request = CreateRequest(CreateConnection(), AiProviderApiFormat.OpenAIResponses);

        var act = () => adapter.CreateChatClient(request);

        act.Should().Throw<NotSupportedException>().WithMessage("*OpenAIResponses*Test model*");
    }

    private static AiProviderConnection CreateConnection()
    {
        var now = DateTimeOffset.UtcNow;
        return new AiProviderConnection(
            Guid.NewGuid(),
            "ollama",
            "Local Ollama",
            AiProviderKind.Ollama,
            new Uri("http://localhost:11434/"),
            AiProviderAuthKind.None,
            new Dictionary<string, string>(),
            JsonObject("{}"),
            now,
            now);
    }

    private static AiProviderClientRequest CreateRequest(
        AiProviderConnection connection,
        AiProviderApiFormat apiFormat)
    {
        var now = DateTimeOffset.UtcNow;
        var profile = new AiModelProfile(
            Guid.NewGuid(),
            connection.Id,
            "Test model",
            apiFormat,
            "llama3.2:latest",
            new AiSamplingOptions(true, 0.25, false, 0.7),
            JsonObject("{}"),
            now,
            now);
        return new AiProviderClientRequest(connection, profile, new Dictionary<string, string>(), false, []);
    }

    private static string ReadFixture(string fixtureName)
        => File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "Infrastructure",
            "AiProviders",
            "Fixtures",
            fixtureName));

    private static IReadOnlyDictionary<string, JsonElement> JsonObject(string json)
        => JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json) ?? [];

    private sealed class FixtureHandler : HttpMessageHandler
    {
        private readonly string _responseJson;
        private readonly IList<CapturedRequest>? _requests;

        public FixtureHandler(string responseJson, IList<CapturedRequest>? requests = null)
        {
            _responseJson = responseJson;
            _requests = requests;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            _requests?.Add(new CapturedRequest(
                request.RequestUri!,
                request.Headers.Authorization?.ToString(),
                request.Headers.ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value.ToArray(),
                    StringComparer.OrdinalIgnoreCase)));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_responseJson, Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed record CapturedRequest(
        Uri Uri,
        string? Authorization,
        IReadOnlyDictionary<string, string[]> Headers);
}
