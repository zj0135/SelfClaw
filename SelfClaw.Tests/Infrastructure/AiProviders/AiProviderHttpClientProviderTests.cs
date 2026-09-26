using SelfClaw.Core.Models;
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
    public void Non_streaming_clients_are_cached_by_canonical_connection_fingerprint()
    {
        using var provider = new AiProviderHttpClientProvider(() => new RecordingHandler());
        var connection = CreateConnection(
            new Uri("https://api.example.test/v1/"),
            "{\"timeout_seconds\":30,\"extra_headers\":{\"X-Title\":\"SelfClaw\",\"HTTP-Referer\":\"https://selfclaw.local\"}}");
        var sameFingerprintDifferentOrder = CreateConnection(
            new Uri("https://api.example.test/v1/"),
            "{\"extra_headers\":{\"HTTP-Referer\":\"https://selfclaw.local\",\"X-Title\":\"SelfClaw\"},\"timeout_seconds\":30}");

        var nonStreaming = provider.GetNonStreamingClient(connection);
        var sameNonStreaming = provider.GetNonStreamingClient(sameFingerprintDifferentOrder);

        nonStreaming.Should().BeSameAs(sameNonStreaming);
        nonStreaming.Timeout.Should().Be(TimeSpan.FromSeconds(30));
        provider.CachedClientCount.Should().Be(1);

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
        provider.CachedClientCount.Should().Be(4);
    }

    [Fact]
    public void CreateTurnClient_returns_distinct_clients_over_one_shared_infinite_timeout_handler()
    {
        var handlerFactory = new TrackingHandlerFactory();
        using var provider = new AiProviderHttpClientProvider(handlerFactory.Create);
        var connection = CreateConnection(new Uri("https://api.example.test/v1/"), "{\"timeout_seconds\":12}");

        using var first = provider.CreateTurnClient(connection, null);
        using var second = provider.CreateTurnClient(connection, null);

        first.Should().NotBeSameAs(second);
        first.Timeout.Should().Be(Timeout.InfiniteTimeSpan);
        second.Timeout.Should().Be(Timeout.InfiniteTimeSpan);
        first.BaseAddress.Should().Be(connection.Endpoint);
        handlerFactory.CreatedCount.Should().Be(1);
        provider.CachedSharedHandlerCount.Should().Be(1);
        provider.CachedClientCount.Should().Be(0);
    }

    [Fact]
    public async Task OpenAi_chat_and_responses_turn_clients_share_the_infinite_timeout_transport()
    {
        using var provider = new AiProviderHttpClientProvider(() => new RecordingHandler());
        var connection = CreateConnection(new Uri("https://api.example.test/v1/"), "{\"timeout_seconds\":12}");
        var adapter = new OpenAiProviderAdapter(
            AiProviderKind.OpenAI,
            httpClientProvider: provider);
        var chatProfile = CreateProfile(connection.Id, AiProviderApiFormat.OpenAIChatCompletions);
        var responsesProfile = CreateProfile(connection.Id, AiProviderApiFormat.OpenAIResponses);
        var secrets = new Dictionary<string, string> { [OpenAiProviderAdapter.ApiKeySecretName] = "test-key" };

        using var chatHttpClient = provider.CreateTurnClient(connection, null);
        using var responsesHttpClient = provider.CreateTurnClient(connection, null);
        using var chatClient = adapter.CreateChatClient(
            new AiProviderClientRequest(connection, chatProfile, secrets, false, []), chatHttpClient);
        using var responsesClient = adapter.CreateChatClient(
            new AiProviderClientRequest(connection, responsesProfile, secrets, false, []), responsesHttpClient);

        chatHttpClient.Timeout.Should().Be(Timeout.InfiniteTimeSpan);
        responsesHttpClient.Timeout.Should().Be(Timeout.InfiniteTimeSpan);
        provider.CachedSharedHandlerCount.Should().Be(1);
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

    [Fact]
    public async Task Shared_streaming_handler_is_reused_and_survives_a_turn_client_disposal()
    {
        using var provider = new AiProviderHttpClientProvider(() => new RecordingHandler());
        var connection = CreateConnection(new Uri("https://api.example.test/v1/"), "{}");

        var handler = provider.GetSharedStreamingHandler(connection);

        provider.GetSharedStreamingHandler(CreateConnection(
            new Uri("https://api.example.test/v1/"),
            "{\"extra_headers\":{\"X-Title\":\"SelfClaw\"}}")).Should().NotBeSameAs(handler);
        provider.GetSharedStreamingHandler(connection).Should().BeSameAs(handler);
        provider.CachedSharedHandlerCount.Should().Be(2);

        var turnClient = provider.CreateTurnClient(connection, null);
        turnClient.Dispose();
        using var followUpClient = new HttpClient(handler);
        using var response = await followUpClient.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "https://api.example.test/v1/models"));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Disposing_a_turn_client_does_not_dispose_the_shared_handler_behind_its_outer_handler()
    {
        var handlerFactory = new TrackingHandlerFactory();
        using var provider = new AiProviderHttpClientProvider(handlerFactory.Create);
        var connection = CreateConnection(new Uri("https://api.example.test/v1/"), "{}");
        var turnClient = provider.CreateTurnClient(connection, new OrderedOuterHandler("outer", new List<string>()));

        turnClient.Dispose();

        handlerFactory.Handlers.Single().DisposeCount.Should().Be(0);
        using var nextTurnClient = provider.CreateTurnClient(connection, null);
        using var response = await nextTurnClient.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "https://api.example.test/v1/models"));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task The_outer_handler_is_the_outermost_link_in_the_turn_chain()
    {
        var order = new List<string>();
        using var provider = new AiProviderHttpClientProvider(() => new OrderedTerminalHandler("inner", order));
        var connection = CreateConnection(new Uri("https://api.example.test/v1/"), "{}");
        using var turnClient = provider.CreateTurnClient(connection, new OrderedOuterHandler("outer", order));

        using var response = await turnClient.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "https://api.example.test/v1/models"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        order.Should().Equal("outer", "inner");
    }

    [Fact]
    public void ReadExtraHeaderNames_is_case_insensitive()
    {
        var connection = CreateConnection(
            new Uri("https://api.example.test/v1/"),
            "{\"extra_headers\":{\"X-Title\":\"SelfClaw\",\"HTTP-Referer\":\"https://selfclaw.local\"}}");

        var names = AiProviderHttpClientProvider.ReadExtraHeaderNames(connection);

        names.Should().Contain("x-title").And.Contain("http-referer");
        names.Should().HaveCount(2);
    }

    [Fact]
    public void Provider_disposal_disposes_the_shared_handlers()
    {
        var handlerFactory = new TrackingHandlerFactory();
        var provider = new AiProviderHttpClientProvider(handlerFactory.Create);

        var handler = (TrackingHandler)provider.GetSharedStreamingHandler(
            CreateConnection(new Uri("https://api.example.test/v1/"), "{}"));

        provider.Dispose();
        handler.DisposeCount.Should().Be(1);
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

    private sealed class OrderedOuterHandler : DelegatingHandler
    {
        private readonly string _name;
        private readonly IList<string> _order;

        public OrderedOuterHandler(string name, IList<string> order)
        {
            _name = name;
            _order = order;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            _order.Add(_name);
            return base.SendAsync(request, cancellationToken);
        }
    }

    private sealed class OrderedTerminalHandler : HttpMessageHandler
    {
        private readonly string _name;
        private readonly IList<string> _order;

        public OrderedTerminalHandler(string name, IList<string> order)
        {
            _name = name;
            _order = order;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            _order.Add(_name);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
        }
    }

    private sealed class TrackingHandlerFactory
    {
        public IList<TrackingHandler> Handlers { get; } = [];

        public int CreatedCount => Handlers.Count;

        public TrackingHandler Create()
        {
            var handler = new TrackingHandler();
            Handlers.Add(handler);
            return handler;
        }
    }

    private sealed class TrackingHandler : HttpMessageHandler
    {
        public int DisposeCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });

        protected override void Dispose(bool disposing)
        {
            DisposeCount++;
            base.Dispose(disposing);
        }
    }

    private sealed record CapturedRequest(
        string? Authorization,
        IReadOnlyDictionary<string, string[]> Headers);
}
