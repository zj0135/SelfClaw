using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using SelfClaw.Infrastructure.AiProviders.Gemini;
using SelfClaw.Infrastructure.AiProviders.Http;
using SelfClaw.Infrastructure.AiProviders.Models;

namespace SelfClaw.Tests.Infrastructure.AiProviders;

public sealed class GeminiProviderAdapterTests
{
    [Fact]
    public void Chat_uses_openai_compatibility_endpoint_and_rejects_native_format_for_now()
    {
        using var httpClientProvider = new AiProviderHttpClientProvider(() => new FixtureHandler("{}"));
        var adapter = new GeminiProviderAdapter(httpClientProvider);
        var connection = CreateConnection();
        var compatibleRequest = CreateRequest(connection, AiProviderApiFormat.OpenAIChatCompletions);

        adapter.ProviderKind.Should().Be(AiProviderKind.GoogleGemini);
        adapter.SupportsModelListing.Should().BeTrue();
        adapter.SupportsApiFormat(AiProviderApiFormat.OpenAIChatCompletions).Should().BeTrue();
        adapter.SupportsApiFormat(AiProviderApiFormat.GeminiGenerateContent).Should().BeFalse();

        using var client = adapter.CreateChatClient(compatibleRequest);
        client.Should().NotBeNull();
        httpClientProvider.CachedClientCount.Should().Be(1);
        httpClientProvider.GetStreamingClient(connection with
        {
            Endpoint = new Uri("https://generativelanguage.googleapis.com/v1beta/openai/")
        });
        httpClientProvider.CachedClientCount.Should().Be(1);

        var unsupported = () => adapter.CreateChatClient(
            CreateRequest(connection, AiProviderApiFormat.GeminiGenerateContent));
        unsupported.Should().Throw<NotSupportedException>().WithMessage("*GeminiGenerateContent*Test model*");
    }

    [Fact]
    public async Task ListModelsAsync_uses_native_endpoint_and_filters_generate_content_models()
    {
        var requests = new List<CapturedRequest>();
        using var httpClientProvider = new AiProviderHttpClientProvider(
            () => new FixtureHandler(ReadFixture("gemini-models.json"), requests));
        var adapter = new GeminiProviderAdapter(httpClientProvider);
        var connection = CreateConnection();

        var models = await adapter.ListModelsAsync(
            connection,
            new Dictionary<string, string> { ["api_key"] = "gemini-key" });

        models.Select(model => model.ModelId).Should().Equal("gemini-2.5-pro", "gemini-2.5-flash");
        models[0].DisplayName.Should().Be("Gemini 2.5 Pro");
        models[0].ContextLength.Should().Be(1048576);
        models[0].MaxOutputTokens.Should().Be(65536);
        requests.Should().ContainSingle();
        requests[0].Uri.Should().Be(new Uri("https://generativelanguage.googleapis.com/v1beta/models"));
        requests[0].Headers["x-goog-api-key"].Should().Equal("gemini-key");
        requests[0].Authorization.Should().BeNull();
    }

    [Fact]
    public async Task ListModelsAsync_requires_api_key_before_sending_request()
    {
        var requests = new List<CapturedRequest>();
        using var httpClientProvider = new AiProviderHttpClientProvider(
            () => new FixtureHandler(ReadFixture("gemini-models.json"), requests));
        var adapter = new GeminiProviderAdapter(httpClientProvider);

        var act = () => adapter.ListModelsAsync(CreateConnection(), new Dictionary<string, string>());

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*api_key*");
        requests.Should().BeEmpty();
    }

    private static AiProviderConnection CreateConnection()
    {
        var now = DateTimeOffset.UtcNow;
        return new AiProviderConnection(
            Guid.NewGuid(),
            "google-gemini",
            "Google Gemini",
            AiProviderKind.GoogleGemini,
            new Uri("https://generativelanguage.googleapis.com/"),
            AiProviderAuthKind.ApiKey,
            new Dictionary<string, string> { ["api_key"] = "secret:gemini" },
            new Dictionary<string, JsonElement>(),
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
            "gemini-2.5-pro",
            new AiSamplingOptions(false, 0.7, false, 0.7),
            new Dictionary<string, JsonElement>(),
            now,
            now);
        return new AiProviderClientRequest(
            connection,
            profile,
            new Dictionary<string, string> { ["api_key"] = "gemini-key" },
            false,
            []);
    }

    private static string ReadFixture(string fixtureName)
        => File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory,
            "Infrastructure",
            "AiProviders",
            "Fixtures",
            fixtureName));

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
