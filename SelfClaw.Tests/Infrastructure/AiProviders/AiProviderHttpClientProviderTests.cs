using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using FluentAssertions;
using SelfClaw.Infrastructure.AiProviders.Http;
using SelfClaw.Infrastructure.AiProviders.Models;
using SelfClaw.Infrastructure.AiProviders.OpenAi;

namespace SelfClaw.Tests.Infrastructure.AiProviders;

public sealed class AiProviderHttpClientProviderTests
{
    [Fact]
    public void Clients_are_cached_by_canonical_connection_fingerprint_and_operation_kind()
    {
        using var provider = new AiProviderHttpClientProvider(() => new RecordingHandler());
        var connection = CreateConnection(
            new Uri("https://api.example.test/v1/"),
            "{\"timeout_seconds\":30,\"extra_headers\":{\"X-Title\":\"SelfClaw\",\"HTTP-Referer\":\"https://selfclaw.local\"}}");
        var sameFingerprintDifferentOrder = CreateConnection(
            new Uri("https://api.example.test/v1/"),
            "{\"extra_headers\":{\"HTTP-Referer\":\"https://selfclaw.local\",\"X-Title\":\"SelfClaw\"},\"timeout_seconds\":30}");

        var streaming = provider.GetStreamingClient(connection);
        var sameStreaming = provider.GetStreamingClient(sameFingerprintDifferentOrder);
        var nonStreaming = provider.GetNonStreamingClient(connection);
        var sameNonStreaming = provider.GetNonStreamingClient(sameFingerprintDifferentOrder);

        streaming.Should().BeSameAs(sameStreaming);
        nonStreaming.Should().BeSameAs(sameNonStreaming);
        streaming.Should().NotBeSameAs(nonStreaming);
        streaming.Timeout.Should().Be(Timeout.InfiniteTimeSpan);
        nonStreaming.Timeout.Should().Be(TimeSpan.FromSeconds(30));
        provider.CachedClientCount.Should().Be(2);

        provider.GetNonStreamingClient(CreateConnection(
            new Uri("https://api.example.test/v1/"),
            "{\"timeout_seconds\":31,\"extra_headers\":{\"X-Title\":\"SelfClaw\",\"HTTP-Referer\":\"https://selfclaw.local\"}}"))
            .Should().NotBeSameAs(nonStreaming);
        provider.GetNonStreamingClient(CreateConnection(
            new Uri("https://other.example.test/v1/"),
            "{\"timeout_seconds\":30,\"extra_headers\":{\"X-Title\":\"SelfClaw\",\"HTTP-Referer\":\"https://selfclaw.local\"}}"))
            .Should().NotBeSameAs(nonStreaming);
        provider.GetNonStreamingClient(CreateConnection(
            new Uri("https://api.example.test/V1/"),
            "{\"timeout_seconds\":30,\"extra_headers\":{\"X-Title\":\"SelfClaw\",\"HTTP-Referer\":\"https://selfclaw.local\"}}"))
            .Should().NotBeSameAs(nonStreaming);
        provider.CachedClientCount.Should().Be(5);
    }

    [Fact]
    public void OpenAi_chat_and_responses_clients_share_the_cached_infinite_timeout_transport()
    {
        using var provider = new AiProviderHttpClientProvider(() => new RecordingHandler());
        var connection = CreateConnection(new Uri("https://api.example.test/v1/"), "{\"timeout_seconds\":12}");
        var adapter = new OpenAiProviderAdapter(
            AiProviderKind.OpenAI,
            httpClientProvider: provider);
        var chatProfile = CreateProfile(connection.Id, AiProviderApiFormat.OpenAIChatCompletions);
        var responsesProfile = CreateProfile(connection.Id, AiProviderApiFormat.OpenAIResponses);
        var secrets = new Dictionary<string, string> { [OpenAiProviderAdapter.ApiKeySecretName] = "test-key" };

        using var chatClient = adapter.CreateChatClient(
            new AiProviderClientRequest(connection, chatProfile, secrets, false, []));
        using var responsesClient = adapter.CreateChatClient(
            new AiProviderClientRequest(connection, responsesProfile, secrets, false, []));

        provider.CachedClientCount.Should().Be(1);
        provider.GetStreamingClient(connection).Timeout.Should().Be(Timeout.InfiniteTimeSpan);
    }

    [Fact]
    public async Task Extra_headers_are_injected_without_overwriting_explicit_request_headers()
    {
        var capturedRequests = new List<CapturedRequest>();
        using var provider = new AiProviderHttpClientProvider(() => new RecordingHandler(capturedRequests));
        var connection = CreateConnection(
            new Uri("https://openrouter.ai/api/v1/"),
            "{\"extra_headers\":{\"HTTP-Referer\":\"https://selfclaw.local\",\"X-Title\":\"SelfClaw\",\"Authorization\":\"Bearer must-not-win\"}}");
        var client = provider.GetNonStreamingClient(connection);
        using var request = new HttpRequestMessage(HttpMethod.Get, "models");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "real-key");

        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        capturedRequests.Should().ContainSingle();
        capturedRequests[0].Headers["HTTP-Referer"].Should().Equal("https://selfclaw.local");
        capturedRequests[0].Headers["X-Title"].Should().Equal("SelfClaw");
        capturedRequests[0].Authorization.Should().Be("Bearer real-key");
    }

    [Fact]
    public void Non_streaming_timeout_defaults_to_one_hundred_seconds()
    {
        using var provider = new AiProviderHttpClientProvider(() => new RecordingHandler());
        var connection = CreateConnection(new Uri("https://api.example.test/v1/"), "{}");

        provider.GetNonStreamingTimeout(connection).Should()
            .Be(TimeSpan.FromSeconds(AiProviderHttpClientProvider.DefaultTimeoutSeconds));
        provider.GetNonStreamingClient(connection).Timeout.Should()
            .Be(TimeSpan.FromSeconds(AiProviderHttpClientProvider.DefaultTimeoutSeconds));
    }

    [Theory]
    [InlineData("{\"timeout_seconds\":0}", "*timeout_seconds*")]
    [InlineData("{\"timeout_seconds\":3601}", "*timeout_seconds*")]
    [InlineData("{\"timeout_seconds\":\"slow\"}", "*timeout_seconds*")]
    [InlineData("{\"extra_headers\":[]}", "*extra_headers*")]
    [InlineData("{\"extra_headers\":{\"X-Title\":42}}", "*X-Title*")]
    public void Invalid_connection_options_fail_with_readable_errors(string optionsJson, string messagePattern)
    {
        using var provider = new AiProviderHttpClientProvider(() => new RecordingHandler());
        var connection = CreateConnection(new Uri("https://api.example.test/v1/"), optionsJson);

        var act = () => provider.GetNonStreamingClient(connection);

        act.Should().Throw<InvalidOperationException>().WithMessage(messagePattern);
    }

    private static AiProviderConnection CreateConnection(Uri endpoint, string optionsJson)
    {
        var now = DateTimeOffset.UtcNow;
        return new AiProviderConnection(
            Guid.NewGuid(),
            "custom",
            "Gateway",
            AiProviderKind.OpenAICompatible,
            endpoint,
            AiProviderAuthKind.ApiKey,
            new Dictionary<string, string>(),
            JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(optionsJson) ?? [],
            now,
            now);
    }

    private static AiModelProfile CreateProfile(Guid connectionId, AiProviderApiFormat apiFormat)
    {
        var now = DateTimeOffset.UtcNow;
        return new AiModelProfile(
            Guid.NewGuid(),
            connectionId,
            "Test model",
            apiFormat,
            "test-model",
            new AiSamplingOptions(false, 0.7, false, 0.7),
            new Dictionary<string, JsonElement>(),
            now,
            now);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly IList<CapturedRequest>? _requests;

        public RecordingHandler(IList<CapturedRequest>? requests = null)
        {
            _requests = requests;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            _requests?.Add(new CapturedRequest(
                request.Headers.Authorization?.ToString(),
                request.Headers.ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value.ToArray(),
                    StringComparer.OrdinalIgnoreCase)));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}")
            });
        }
    }

    private sealed record CapturedRequest(
        string? Authorization,
        IReadOnlyDictionary<string, string[]> Headers);
}
