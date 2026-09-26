using SelfClaw.Core.Models;
using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.AI;
using SelfClaw.Infrastructure.AiProviders.Abstractions;
using SelfClaw.Infrastructure.AiProviders.Anthropic;
using SelfClaw.Infrastructure.AiProviders.Http;
using SelfClaw.Infrastructure.AiProviders.Models;
using SelfClaw.Infrastructure.AiProviders.Ollama;
using SelfClaw.Infrastructure.AiProviders.OpenAi;
using SelfClaw.Infrastructure.Agents.Direct.Tools;

namespace SelfClaw.Tests.Infrastructure.AiProviders;

public sealed class AiModelConfigurationRequestTests
{
    [Theory]
    [InlineData(AiProviderApiFormat.OpenAIChatCompletions)]
    [InlineData(AiProviderApiFormat.OpenAIResponses)]
    [InlineData(AiProviderApiFormat.AnthropicMessages)]
    [InlineData(AiProviderApiFormat.OllamaNative)]
    public async Task Provider_serialization_preserves_the_bounded_tool_contract_without_display_duplication(AiProviderApiFormat format)
    {
        var handler = new RecordingHandler(ResponseFor(format));
        using var http = new AiProviderHttpClientProvider(() => handler);
        IAiProviderAdapter adapter = format switch
        {
            AiProviderApiFormat.AnthropicMessages => new AnthropicProviderAdapter(httpClientProvider: http),
            AiProviderApiFormat.OllamaNative => new OllamaProviderAdapter(http),
            _ => new OpenAiProviderAdapter(httpClientProvider: http)
        };
        var request = CreateRequest(adapter.ProviderKind, format, null);
        var result = McpToolResultFormatter.Format(JsonSerializer.SerializeToElement(new
        {
            content = new[] { new { type = "text", text = new string('"', 70_000) } }, isError = true
        })) with { Detail = "DISPLAY ONLY SENTINEL" };
        using var httpClient = http.CreateTurnClient(request.Connection, null);
        using var client = adapter.CreateChatClient(request, httpClient);
        await client.GetResponseAsync(
        [
            new ChatMessage(ChatRole.User, "look up"),
            new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("call-1", "lookup", new Dictionary<string, object?>())]),
            new ChatMessage(ChatRole.Tool, [new FunctionResultContent("call-1", result)])
        ], adapter.CreateChatOptions(request));

        var payload = ReadStrings(handler.Body).Single(text => text.Contains("[SelfClaw truncated"));
        payload.Should().NotContain("DISPLAY ONLY SENTINEL");
        Encoding.UTF8.GetByteCount(payload).Should().BeLessThanOrEqualTo(McpToolResultFormatter.MaximumModelResultBytes);
        using var document = JsonDocument.Parse(payload);
        document.RootElement.ToString().Should().Contain("Failed");
    }

    private static IEnumerable<string> ReadStrings(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            yield return element.GetString() ?? string.Empty;
        }
        else if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
                foreach (var text in ReadStrings(property.Value)) yield return text;
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                foreach (var text in ReadStrings(item)) yield return text;
        }
    }

    [Fact]
    public async Task Anthropic_streaming_preserves_thinking_and_text_with_shared_options()
    {
        const string stream = """
            event: message_start
            data: {"type":"message_start","message":{"id":"msg-test","type":"message","role":"assistant","model":"test-model","content":[],"stop_reason":null,"stop_sequence":null,"usage":{"input_tokens":1,"output_tokens":0}}}

            event: content_block_start
            data: {"type":"content_block_start","index":0,"content_block":{"type":"thinking","thinking":"","signature":""}}

            event: content_block_delta
            data: {"type":"content_block_delta","index":0,"delta":{"type":"thinking_delta","thinking":"Considering"}}

            event: content_block_delta
            data: {"type":"content_block_delta","index":0,"delta":{"type":"signature_delta","signature":"test-signature"}}

            event: content_block_stop
            data: {"type":"content_block_stop","index":0}

            event: content_block_start
            data: {"type":"content_block_start","index":1,"content_block":{"type":"text","text":""}}

            event: content_block_delta
            data: {"type":"content_block_delta","index":1,"delta":{"type":"text_delta","text":"Answer"}}

            event: content_block_stop
            data: {"type":"content_block_stop","index":1}

            event: message_delta
            data: {"type":"message_delta","delta":{"stop_reason":"end_turn","stop_sequence":null},"usage":{"output_tokens":2}}

            event: message_stop
            data: {"type":"message_stop"}


            """;
        var handler = new RecordingHandler(stream, "text/event-stream");
        using var http = new AiProviderHttpClientProvider(() => handler);
        var adapter = new AnthropicProviderAdapter(httpClientProvider: http);
        var request = CreateRequest(adapter.ProviderKind, AiProviderApiFormat.AnthropicMessages, "high");
        using var httpClient = http.CreateTurnClient(request.Connection, null);
        using var client = adapter.CreateChatClient(request, httpClient);
        var contents = new List<AIContent>();
        await foreach (var update in client.GetStreamingResponseAsync(
                           [new ChatMessage(ChatRole.User, "Hello")], adapter.CreateChatOptions(request)))
        {
            contents.AddRange(update.Contents);
        }

        contents.OfType<TextReasoningContent>().Should().Contain(content => content.Text == "Considering");
        contents.OfType<TextContent>().Should().Contain(content => content.Text == "Answer");
        handler.Body.GetProperty("stream").GetBoolean().Should().BeTrue();
        handler.Body.GetProperty("output_config").GetProperty("effort").GetString().Should().Be("high");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("none")]
    [InlineData("minimal")]
    [InlineData("low")]
    [InlineData("medium")]
    [InlineData("high")]
    [InlineData("xhigh")]
    [InlineData("max")]
    public async Task Compatible_endpoint_uses_configured_effort_and_provider_default_omits_legacy_thinking(string? effort)
    {
        var handler = new RecordingHandler(ResponseFor(AiProviderApiFormat.OpenAIChatCompletions));
        using var http = new AiProviderHttpClientProvider(() => handler);
        var adapter = new OpenAiProviderAdapter(AiProviderKind.OpenAICompatible, httpClientProvider: http);
        var request = CreateRequest(adapter.ProviderKind, AiProviderApiFormat.OpenAIChatCompletions, effort);
        using var httpClient = http.CreateTurnClient(request.Connection, null);
        using var client = adapter.CreateChatClient(request, httpClient);

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "Hello")], adapter.CreateChatOptions(request));

        if (effort is null)
        {
            handler.Body.TryGetProperty("thinking", out _).Should().BeFalse();
            handler.Body.TryGetProperty("reasoning_effort", out _).Should().BeFalse();
        }
        else
        {
            handler.Body.GetProperty("thinking").GetProperty("type").GetString().Should().Be(effort == "none" ? "disabled" : "enabled");
            handler.Body.GetProperty("reasoning_effort").GetString().Should().Be(effort);
        }
    }

    [Theory]
    [InlineData(AiProviderApiFormat.OpenAIChatCompletions, "max")]
    [InlineData(AiProviderApiFormat.OpenAIResponses, "high")]
    [InlineData(AiProviderApiFormat.AnthropicMessages, "max")]
    [InlineData(AiProviderApiFormat.OllamaNative, "medium")]
    [InlineData(AiProviderApiFormat.OpenAIChatCompletions, "none")]
    [InlineData(AiProviderApiFormat.OpenAIResponses, "none")]
    [InlineData(AiProviderApiFormat.AnthropicMessages, "none")]
    [InlineData(AiProviderApiFormat.OllamaNative, "none")]
    [InlineData(AiProviderApiFormat.OpenAIChatCompletions, null)]
    [InlineData(AiProviderApiFormat.OpenAIResponses, null)]
    [InlineData(AiProviderApiFormat.AnthropicMessages, null)]
    [InlineData(AiProviderApiFormat.OllamaNative, null)]
    public async Task Provider_requests_serialize_shared_configuration(AiProviderApiFormat format, string? effort)
    {
        var handler = new RecordingHandler(ResponseFor(format));
        using var http = new AiProviderHttpClientProvider(() => handler);
        IAiProviderAdapter adapter = format switch
        {
            AiProviderApiFormat.AnthropicMessages => new AnthropicProviderAdapter(httpClientProvider: http),
            AiProviderApiFormat.OllamaNative => new OllamaProviderAdapter(http),
            _ => new OpenAiProviderAdapter(httpClientProvider: http)
        };
        var request = CreateRequest(adapter.ProviderKind, format, effort);
        using var httpClient = http.CreateTurnClient(request.Connection, null);
        using var client = adapter.CreateChatClient(request, httpClient);
        var options = adapter.CreateChatOptions(request);

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "Hello")], options);

        options.MaxOutputTokens.Should().Be(16000);
        var body = handler.Body;
        body.GetProperty("model").GetString().Should().Be(request.Profile.Model);
        body.GetRawText().Should().NotContain("priceInPerMTok").And.NotContain("Billing notes");
        if (format == AiProviderApiFormat.OllamaNative)
        {
            body.GetProperty("options").GetProperty("num_predict").GetInt32().Should().Be(16000);
            body.GetProperty("options").GetProperty("num_ctx").GetInt32().Should().Be(128000);
            body.GetProperty("options").GetProperty("temperature").GetDouble().Should().BeApproximately(0.4, 0.001);
            if (effort is null) body.TryGetProperty("think", out _).Should().BeFalse();
            else if (effort == "none") body.GetProperty("think").GetBoolean().Should().BeFalse();
            else body.GetProperty("think").GetString().Should().Be(effort);
        }
        else
        {
            body.GetProperty("temperature").GetDouble().Should().BeApproximately(0.4, 0.001);
            body.TryGetProperty("top_p", out _).Should().BeFalse();
            AssertReasoningAndLimits(body, format, effort);
        }
    }

    private static void AssertReasoningAndLimits(JsonElement body, AiProviderApiFormat format, string? effort)
    {
        switch (format)
        {
            case AiProviderApiFormat.OpenAIChatCompletions:
                body.GetProperty("max_completion_tokens").GetInt32().Should().Be(16000);
                if (effort is null) body.TryGetProperty("reasoning_effort", out _).Should().BeFalse();
                else body.GetProperty("reasoning_effort").GetString().Should().Be(effort);
                body.TryGetProperty("thinking", out _).Should().BeFalse();
                break;
            case AiProviderApiFormat.OpenAIResponses:
                body.GetProperty("max_output_tokens").GetInt32().Should().Be(16000);
                if (effort is null) body.TryGetProperty("reasoning", out _).Should().BeFalse();
                else body.GetProperty("reasoning").GetProperty("effort").GetString().Should().Be(effort);
                break;
            case AiProviderApiFormat.AnthropicMessages:
                body.GetProperty("max_tokens").GetInt32().Should().Be(16000);
                if (effort is null) body.TryGetProperty("thinking", out _).Should().BeFalse();
                else body.GetProperty("thinking").GetProperty("type").GetString().Should().Be(effort == "none" ? "disabled" : "adaptive");
                if (effort is not (null or "none")) body.GetProperty("output_config").GetProperty("effort").GetString().Should().Be(effort);
                break;
        }
    }

    private static AiProviderClientRequest CreateRequest(AiProviderKind kind, AiProviderApiFormat format, string? effort)
    {
        var now = DateTimeOffset.UtcNow;
        var connection = new AiProviderConnection(Guid.NewGuid(), "custom", "Test", kind,
            new Uri("https://api.example.test/"), AiProviderAuthKind.ApiKey,
            new Dictionary<string, string>(), new Dictionary<string, JsonElement>(), now, now);
        var configuration = new AiModelConfiguration("test-model", "Test", new AiSamplingOptions(true, 0.4, false, 0.9),
            true, effort, 128000, 16000, 1m, 2m, 0.1m, 0.2m, "Billing notes");
        var profile = new AiModelProfile(Guid.NewGuid(), connection.Id, "Test", format, configuration.Model,
            new AiSamplingOptions(false, 1, true, 0.5), new Dictionary<string, JsonElement>(), now, now,
            Configuration: configuration);
        return new AiProviderClientRequest(connection, profile, new Dictionary<string, string> { ["api_key"] = "test-key" }, false, []);
    }

    private static string ResponseFor(AiProviderApiFormat format) => format switch
    {
        AiProviderApiFormat.OpenAIChatCompletions => """
            {"id":"chatcmpl-test","object":"chat.completion","created":1,"model":"test-model","choices":[{"index":0,"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}]}
            """,
        AiProviderApiFormat.OpenAIResponses => """
            {"id":"resp-test","object":"response","created_at":1,"model":"test-model","status":"completed","output":[{"id":"msg-test","type":"message","role":"assistant","status":"completed","content":[{"type":"output_text","text":"ok","annotations":[]}]}]}
            """,
        AiProviderApiFormat.AnthropicMessages => """
            {"id":"msg-test","type":"message","role":"assistant","model":"test-model","content":[{"type":"text","text":"ok"}],"stop_reason":"end_turn","stop_sequence":null,"usage":{"input_tokens":1,"output_tokens":1}}
            """,
        _ => """
            {"model":"test-model","created_at":"2026-01-01T00:00:00Z","message":{"role":"assistant","content":"ok"},"done":true,"done_reason":"stop"}
            """
    };

    private sealed class RecordingHandler(string response, string contentType = "application/json") : HttpMessageHandler
    {
        public JsonElement Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request.Content);
            using var document = JsonDocument.Parse(await request.Content.ReadAsStringAsync(cancellationToken));
            Body = document.RootElement.Clone();
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response, Encoding.UTF8, contentType) };
        }
    }
}
