using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using SelfClaw.Infrastructure.Agents.Direct.Hooks;
using SelfClaw.Infrastructure.Agents.Direct.Hooks.Models;
using SelfClaw.Infrastructure.Extensions.Plugins.Models;
using SelfClaw.Tests.Infrastructure.Agents.Direct.Hooks.TestDoubles;

namespace SelfClaw.Tests.Infrastructure.Agents.Direct.Hooks;

public sealed class HttpHookHandlerTests
{
    [Fact]
    public async Task A_whitelisted_header_is_added_before_the_request_travels()
    {
        var payloads = new List<JsonElement>();
        var inner = new RecordingHandler();
        using var handler = CreateHandler(
            inner,
            [Response("""{"addHeaders":{"x-trace":"abc"}}""")],
            payloads);

        await SendAsync(handler);

        inner.LastRequest!.Headers.GetValues("x-trace").Should().Equal("abc");
    }

    [Fact]
    public async Task An_existing_or_reserved_header_is_not_overwritten()
    {
        var inner = new RecordingHandler();
        using var handler = CreateHandler(
            inner,
            [Response("""{"addHeaders":{"authorization":"Bearer injected","x-extra":"value"}}""")],
            [],
            reserved: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "x-extra" });
        var request = new HttpRequestMessage(HttpMethod.Get, "https://api.example.com/v1");
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer original");

        using var invoker = new HttpMessageInvoker(handler, disposeHandler: false);
        using var response = await invoker.SendAsync(request, CancellationToken.None);

        inner.LastRequest!.Headers.GetValues("Authorization").Should().Equal("Bearer original");
        inner.LastRequest.Headers.Contains("x-extra").Should().BeFalse();
    }

    [Fact]
    public async Task A_transport_failure_delivers_httpResponseReceived_with_the_error()
    {
        var enqueued = new List<AsyncHookWork>();
        using var handler = CreateHandler(
            new ThrowingHandler(new HttpRequestException("connection reset")),
            [],
            [],
            enqueued);

        var action = async () =>
        {
            using var invoker = new HttpMessageInvoker(handler, disposeHandler: false);
            using var request = new Request();
            await invoker.SendAsync(request.Message, CancellationToken.None);
        };

        await action.Should().ThrowAsync<HttpRequestException>();
        enqueued.Should().ContainSingle().Which.EventName.Should().Be("httpResponseReceived");
        TheError(enqueued[0]).Should().Be("connection reset");
    }

    [Fact]
    public async Task An_sdk_timeout_is_reported_as_timeout_but_a_turn_cancellation_is_not_delivered()
    {
        var enqueued = new List<AsyncHookWork>();
        using var turnCancellation = new CancellationTokenSource();
        using var handler = CreateHandler(
            new ThrowingHandler(new OperationCanceledException("sdk timeout")),
            [],
            [],
            enqueued,
            turnCancellation.Token);

        var action = async () =>
        {
            using var invoker = new HttpMessageInvoker(handler, disposeHandler: false);
            using var request = new Request();
            await invoker.SendAsync(request.Message, CancellationToken.None);
        };

        await action.Should().ThrowAsync<OperationCanceledException>();
        TheError(enqueued.Should().ContainSingle().Subject).Should().Be("timeout");

        enqueued.Clear();
        await turnCancellation.CancelAsync();
        await action.Should().ThrowAsync<OperationCanceledException>();
        enqueued.Should().BeEmpty();
    }

    [Fact]
    public async Task The_request_body_is_only_included_when_the_hook_asks_for_it()
    {
        var payloads = new List<JsonElement>();
        using var handler = CreateHandler(
            new RecordingHandler(),
            [Response("")],
            payloads,
            includeRequestBody: true);
        await SendAsync(handler, new string('b', 100));

        payloads.Should().ContainSingle();
        payloads[0].GetProperty("body").GetString()!.Length.Should().Be(100);
    }

    [Fact]
    public async Task The_request_body_is_omitted_without_the_flag()
    {
        var payloads = new List<JsonElement>();
        using var handler = CreateHandler(
            new RecordingHandler(),
            [Response("")],
            payloads,
            includeRequestBody: false);
        await SendAsync(handler, "content");

        payloads[0].TryGetProperty("body", out _).Should().BeFalse();
    }

    [Fact]
    public async Task Every_real_request_gets_the_next_sequence_number()
    {
        var payloads = new List<JsonElement>();
        using var handler = CreateHandler(new RecordingHandler(), [Response("")], payloads);

        await SendAsync(handler);
        await SendAsync(handler);

        payloads.Select(payload => payload.GetProperty("requestSequence").GetInt32()).Should().Equal(1, 2);
    }

    private static string? TheError(AsyncHookWork work)
        => JsonDocument.Parse(work.Payload).RootElement.GetProperty("error").GetString();

    private static HttpHookHandler CreateHandler(
        HttpMessageHandler inner,
        IReadOnlyList<HookSpec> hooks,
        List<JsonElement> payloads,
        List<AsyncHookWork>? enqueued = null,
        CancellationToken turnCancellation = default,
        bool includeRequestBody = false,
        IReadOnlySet<string>? reserved = null)
    {
        var resolved = hooks
            .Select(spec => HookTestFactory.CreateHook(
                "alpha",
                spec.HookId,
                PluginHookEvent.HttpRequestSending,
                includeRequestBody: includeRequestBody || spec.IncludeRequestBody))
            .Concat(enqueued is null
                ? []
                : [HookTestFactory.CreateHook("alpha", "response", PluginHookEvent.HttpResponseReceived)])
            .ToArray();
        var stdout = hooks.ToDictionary(spec => spec.HookId, spec => spec.Stdout, StringComparer.Ordinal);
        var captured = payloads;
        var turnHooks = HookTestFactory.Create(
            resolved,
            (_, payload, _, _) =>
            {
                if (captured is not null && payload.Length > 0)
                {
                    lock (captured)
                    {
                        captured.Add(JsonDocument.Parse(payload).RootElement.Clone());
                    }
                }

                return Task.FromResult(HookTestFactory.Success(
                    hooks.Count == 0 ? string.Empty : stdout[hooks[0].HookId]));
            },
            work =>
            {
                enqueued?.Add(work);
                return true;
            });
        var handler = new HttpHookHandler(
            turnHooks,
            reserved ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            turnCancellation);
        handler.InnerHandler = inner;
        return handler;
    }

    private static async Task SendAsync(HttpHookHandler handler, string? body = null)
    {
        using var invoker = new HttpMessageInvoker(handler, disposeHandler: false);
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.example.com/v1");
        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        using var response = await invoker.SendAsync(request, CancellationToken.None);
    }

    private sealed record HookSpec(string HookId, string Stdout, bool IncludeRequestBody = false);

    private static HookSpec Response(string stdout) => new("a", stdout);

    private sealed class Request : IDisposable
    {
        public HttpRequestMessage Message { get; } = new(HttpMethod.Get, "https://api.example.com/v1");

        public void Dispose() => Message.Dispose();
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class ThrowingHandler(Exception exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromException<HttpResponseMessage>(exception);
    }
}
