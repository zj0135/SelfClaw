using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.AI;
using SelfClaw.Core.Models;
using SelfClaw.Infrastructure.Agents.Direct.Hooks;
using SelfClaw.Infrastructure.Agents.Direct.Hooks.Models;
using SelfClaw.Infrastructure.AiProviders.Http;
using SelfClaw.Infrastructure.AiProviders.Models;
using SelfClaw.Infrastructure.AiProviders.OpenAi;
using SelfClaw.Infrastructure.Extensions.Plugins.Models;
using SelfClaw.Tests.Infrastructure.Agents.Direct.Hooks.TestDoubles;

namespace SelfClaw.Tests.Infrastructure.Agents.Direct.Hooks;

public sealed class HttpHookHandlerSdkRetryTests
{
    [Fact]
    public async Task An_SDK_retry_triggers_httpRequestSending_once_per_real_request()
    {
        const string sse =
            "data: {\"id\":\"c1\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"test-model\","
            + "\"choices\":[{\"index\":0,\"delta\":{\"content\":\"hi\"},\"finish_reason\":null}]}\n\n"
            + "data: {\"id\":\"c1\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"test-model\","
            + "\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n"
            + "data: [DONE]\n\n";
        var payloads = new List<JsonElement>();
        var hook = HookTestFactory.CreateHook(
            "alpha",
            "a",
            PluginHookEvent.HttpRequestSending,
            command: "unused.exe");
        var turnHooks = HookTestFactory.Create(
            [hook],
            (_, payload, _, _) =>
            {
                lock (payloads)
                {
                    payloads.Add(JsonDocument.Parse(payload).RootElement.Clone());
                }

                return Task.FromResult(HookTestFactory.Success(""));
            });
        var primary = new RetryOnceHandler(sse);
        using var provider = new AiProviderHttpClientProvider(() => primary);
        var request = CreateRequest();
        using var hookHandler = new HttpHookHandler(
            turnHooks,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            CancellationToken.None);
        using var httpClient = provider.CreateTurnClient(request.Connection, hookHandler);
        var adapter = new OpenAiProviderAdapter(
            AiProviderKind.OpenAICompatible,
            httpClientProvider: provider);
        var client = adapter.CreateChatClient(request, httpClient);
        var options = adapter.CreateChatOptions(request);

        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in client.GetStreamingResponseAsync(
                           [new ChatMessage(ChatRole.User, "hi")], options))
        {
            updates.Add(update);
        }

        primary.Attempts.Should().Be(2);
        updates.SelectMany(update => update.Contents).OfType<TextContent>().Select(text => text.Text)
            .Should().Contain("hi");
        payloads.Select(payload => payload.GetProperty("requestSequence").GetInt32()).Should().Equal(1, 2);
        payloads.Should().OnlyContain(payload =>
            payload.GetProperty("event").GetString() == "httpRequestSending");
    }

    private static AiProviderClientRequest CreateRequest()
    {
        var now = DateTimeOffset.UtcNow;
        var connection = new AiProviderConnection(
            Guid.NewGuid(),
            "test",
            "OpenAICompatible",
            AiProviderKind.OpenAICompatible,
            new Uri("https://api.example.test/v1/"),
            AiProviderAuthKind.ApiKey,
            new Dictionary<string, string>
            {
                [OpenAiProviderAdapter.ApiKeySecretName] = "secret:test"
            },
            new Dictionary<string, JsonElement>(),
            now,
            now);
        var profile = new AiModelProfile(
            Guid.NewGuid(),
            connection.Id,
            "Test profile",
            AiProviderApiFormat.OpenAIChatCompletions,
            "test-model",
            new AiSamplingOptions(false, 0.7, false, 0.7),
            new Dictionary<string, JsonElement>(),
            now,
            now);
        return new AiProviderClientRequest(
            connection,
            profile,
            new Dictionary<string, string> { [OpenAiProviderAdapter.ApiKeySecretName] = "test-api-key" },
            EnableReasoning: false,
            []);
    }

    private sealed class RetryOnceHandler(string sse) : HttpMessageHandler
    {
        public int Attempts { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Attempts++;
            if (Attempts == 1)
            {
                var unavailable = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                {
                    Content = new StringContent("{\"error\":\"busy\"}", Encoding.UTF8, "application/json")
                };
                unavailable.Headers.TryAddWithoutValidation("retry-after", "0");
                return Task.FromResult(unavailable);
            }

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(sse, Encoding.UTF8)
            };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
            return Task.FromResult(response);
        }
    }
}
