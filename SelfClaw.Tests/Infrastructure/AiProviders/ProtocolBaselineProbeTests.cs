using SelfClaw.Core.Runtime;
using SelfClaw.Core.Runtime.Agent;
using SelfClaw.Infrastructure.Agents.Direct.Abstractions;
using SelfClaw.Infrastructure.Agents.Direct.Capabilities;
using SelfClaw.Infrastructure.Agents.Direct.Tools.Models;
using SelfClaw.Tests.Infrastructure.Agents.Direct;
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

namespace SelfClaw.Tests.Infrastructure.AiProviders;

/// <summary>
/// P0 protocol baseline probes. These tests drive the real provider adapters against recorded
/// wire fixtures and dump the resulting Microsoft.Extensions.AI update shape to a text file so
/// the manual Direct loop can be written against the actual contract instead of assumptions.
/// </summary>
public sealed class ProtocolBaselineProbeTests
{
    private static string ProbeDirectory
    {
        get
        {
            var directory = Path.Combine(AppContext.BaseDirectory, "p0-probes");
            Directory.CreateDirectory(directory);
            return directory;
        }
    }

    [Fact]
    public async Task OpenAi_chat_completions_fragmented_tool_arguments_probe()
    {
        using var http = new AiProviderHttpClientProvider(() => new SseHandler(BuildChatCompletionsSse()));
        var adapter = new OpenAiProviderAdapter(AiProviderKind.OpenAICompatible, httpClientProvider: http);
        var request = CreateOpenAiRequest(AiProviderApiFormat.OpenAIChatCompletions);
        using var httpClient = http.CreateTurnClient(request.Connection, null);
        var client = adapter.CreateChatClient(request, httpClient);
        var options = adapter.CreateChatOptions(request, []);

        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in client.GetStreamingResponseAsync(
                           [new ChatMessage(ChatRole.User, "hi")], options))
        {
            updates.Add(update);
        }

        WriteProbe("openai-chat-completions.txt",
            DescribeUpdates(updates) + "\n=== COALESCED ===\n" + DescribeResponse(updates.ToChatResponse()));
        var response = updates.ToChatResponse();
        var calls = response.Messages[0].Contents.OfType<FunctionCallContent>().ToArray();
        calls.Should().HaveCount(2);
        var finishIndex = updates.FindIndex(update => update.FinishReason == ChatFinishReason.ToolCalls);
        var callIndex = updates.FindIndex(update => update.Contents.OfType<FunctionCallContent>().Any());
        finishIndex.Should().BeGreaterThanOrEqualTo(0);
        callIndex.Should().BeGreaterThan(finishIndex, "complete calls arrive after the finish reason");
        calls.Single(call => call.CallId == "call_a").Name.Should().Be("read_file");
        calls.Single(call => call.CallId == "call_a").Arguments!["relativePath"]!.ToString().Should().Be("a.txt");
        calls.Single(call => call.CallId == "call_b").Arguments.Should().BeEmpty();
        response.Messages[0].Contents.OfType<TextReasoningContent>().Should().ContainSingle()
            .Which.Text.Should().Be("think more");
        response.FinishReason.Should().Be(ChatFinishReason.ToolCalls);
        response.Usage!.InputTokenCount.Should().Be(11);
        response.Usage!.OutputTokenCount.Should().Be(7);
        response.Usage!.ReasoningTokenCount.Should().Be(3);
        response.Usage!.TotalTokenCount.Should().Be(18);
    }

    [Fact]
    public async Task Anthropic_messages_thinking_signature_and_tool_arguments_probe()
    {
        using var http = new AiProviderHttpClientProvider(() => new SseHandler(BuildAnthropicSse()));
        var adapter = new AnthropicProviderAdapter(httpClientProvider: http);
        var request = CreateAnthropicRequest();
        using var httpClient = http.CreateTurnClient(request.Connection, null);
        var client = adapter.CreateChatClient(request, httpClient);
        var options = adapter.CreateChatOptions(request, []);

        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in client.GetStreamingResponseAsync(
                           [new ChatMessage(ChatRole.User, "hi")], options))
        {
            updates.Add(update);
        }

        WriteProbe("anthropic-messages.txt",
            DescribeUpdates(updates) + "\n=== COALESCED ===\n" + DescribeResponse(updates.ToChatResponse()));
        var response = updates.ToChatResponse();
        var reasoning = response.Messages[0].Contents.OfType<TextReasoningContent>().Should().ContainSingle().Subject;
        reasoning.Text.Should().Be("let me think");
        GetProtectedData(reasoning).Should().Be("sig-abc",
            "the thinking signature must survive coalescing so the next request can replay it");
        var call = response.Messages[0].Contents.OfType<FunctionCallContent>().Should().ContainSingle().Subject;
        call.CallId.Should().Be("toolu_1");
        call.Name.Should().Be("read_file");
        call.Arguments!["relativePath"]!.ToString().Should().Be("a.txt");
        response.FinishReason.Should().Be(ChatFinishReason.ToolCalls);
        response.Usage!.InputTokenCount.Should().Be(10);
        response.Usage!.OutputTokenCount.Should().Be(15);
        response.Usage!.TotalTokenCount.Should().Be(25);
    }

    [Fact]
    public async Task OpenAi_responses_fragmented_tool_arguments_probe()
    {
        using var http = new AiProviderHttpClientProvider(() => new SseHandler(BuildResponsesSse()));
        var adapter = new OpenAiProviderAdapter(AiProviderKind.OpenAI, httpClientProvider: http);
        var request = CreateOpenAiRequest(AiProviderApiFormat.OpenAIResponses);
        using var httpClient = http.CreateTurnClient(request.Connection, null);
        var client = adapter.CreateChatClient(request, httpClient);
        var options = adapter.CreateChatOptions(request, []);

        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in client.GetStreamingResponseAsync(
                           [new ChatMessage(ChatRole.User, "hi")], options))
        {
            updates.Add(update);
        }

        WriteProbe("openai-responses.txt",
            DescribeUpdates(updates) + "\n=== COALESCED ===\n" + DescribeResponse(updates.ToChatResponse()));
        var response = updates.ToChatResponse();
        var call = response.Messages[0].Contents.OfType<FunctionCallContent>().Should().ContainSingle().Subject;
        call.CallId.Should().Be("call_a");
        call.Arguments!["relativePath"]!.ToString().Should().Be("a.txt");
        response.Messages[0].Contents.OfType<TextReasoningContent>().Should().ContainSingle()
            .Which.Text.Should().Be("think ");
        response.FinishReason.Should().Be(ChatFinishReason.ToolCalls);
        response.Usage!.InputTokenCount.Should().Be(11);
        response.Usage!.OutputTokenCount.Should().Be(7);
    }

    [Fact]
    public async Task Ollama_native_tool_call_probe()
    {
        using var http = new AiProviderHttpClientProvider(() => new SseHandler(BuildOllamaNdjson(), "application/x-ndjson"));
        var adapter = new OllamaProviderAdapter(http);
        var request = CreateOllamaRequest();
        using var httpClient = http.CreateTurnClient(request.Connection, null);
        var client = adapter.CreateChatClient(request, httpClient);
        var options = adapter.CreateChatOptions(request, []);

        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in client.GetStreamingResponseAsync(
                           [new ChatMessage(ChatRole.User, "hi")], options))
        {
            updates.Add(update);
        }

        WriteProbe("ollama-native.txt",
            DescribeUpdates(updates) + "\n=== COALESCED ===\n" + DescribeResponse(updates.ToChatResponse()));
        var response = updates.ToChatResponse();
        var call = response.Messages[0].Contents.OfType<FunctionCallContent>().Should().ContainSingle().Subject;
        call.Name.Should().Be("read_file");
        call.Arguments!["relativePath"]!.ToString().Should().Be("a.txt");
        response.FinishReason.Should().Be(ChatFinishReason.Stop);
        call.CallId.Should().NotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData(AiProviderApiFormat.AnthropicMessages)]
    [InlineData(AiProviderApiFormat.OpenAIChatCompletions)]
    [InlineData(AiProviderApiFormat.OpenAIResponses)]
    public async Task Native_response_and_tool_results_serialize_in_second_http_request(AiProviderApiFormat format)
    {
        var handler = new CapturingHandler(format switch
        {
            AiProviderApiFormat.AnthropicMessages => BuildAnthropicSse(),
            AiProviderApiFormat.OpenAIResponses => BuildResponsesSse(),
            _ => BuildChatCompletionsSse()
        });
        using var http = new AiProviderHttpClientProvider(() => handler);
        IAiProviderAdapter adapter = format == AiProviderApiFormat.AnthropicMessages
            ? new AnthropicProviderAdapter(httpClientProvider: http)
            : new OpenAiProviderAdapter(httpClientProvider: http);
        var request = format == AiProviderApiFormat.AnthropicMessages
            ? CreateAnthropicRequest() : CreateOpenAiRequest(format);
        using var httpClient = http.CreateTurnClient(request.Connection, null);
        using var client = adapter.CreateChatClient(request, httpClient);
        var options = adapter.CreateChatOptions(request, []);
        // Full native aggregation; no display models participate in the replay.
        var history = new List<ChatMessage> { new(ChatRole.User, "hi") };
        var response = await client.GetStreamingResponseAsync(history, options).ToChatResponseAsync();
        history.AddRange(response.Messages);
        var calls = response.Messages.SelectMany(message => message.Contents).OfType<FunctionCallContent>().ToArray();
        history.Add(new ChatMessage(ChatRole.Tool,
            calls.Select(call => (AIContent)new FunctionResultContent(call.CallId, "fixture-result")).ToList()));
        await foreach (var update in client.GetStreamingResponseAsync(history, options)) { }
        handler.Bodies.Should().HaveCount(2);
        WriteProbe($"second-request-{format}.json", handler.Bodies[1]);
        AssertSecondRequest(format, JsonDocument.Parse(handler.Bodies[1]).RootElement);
    }

    [Theory]
    [InlineData(AiProviderApiFormat.AnthropicMessages)]
    [InlineData(AiProviderApiFormat.OpenAIChatCompletions)]
    [InlineData(AiProviderApiFormat.OpenAIResponses)]
    public async Task Explicit_loop_replays_native_content_in_second_http_request(AiProviderApiFormat format)
    {
        var first = format switch
        {
            AiProviderApiFormat.AnthropicMessages => BuildAnthropicSse(),
            AiProviderApiFormat.OpenAIResponses => BuildResponsesSse(),
            _ => BuildChatCompletionsSse()
        };
        var handler = new CapturingHandler(first, FinalSse(format));
        using var http = new AiProviderHttpClientProvider(() => handler);
        IAiProviderAdapter adapter = format == AiProviderApiFormat.AnthropicMessages
            ? new AnthropicProviderAdapter(httpClientProvider: http)
            : new OpenAiProviderAdapter(httpClientProvider: http);
        var preparation = format == AiProviderApiFormat.AnthropicMessages
            ? CreateAnthropicRequest() : CreateOpenAiRequest(format);
        using var httpClient = http.CreateTurnClient(preparation.Connection, null);
        var native = adapter.CreateChatClient(preparation, httpClient);
        var count = 0;
        DirectToolResult Result()
        {
            count++;
            return new DirectToolResult(
                ToolCallStatus.Completed, "fixture-result",
                JsonSerializer.SerializeToElement("fixture-result"), "fixture-result");
        }
        AIFunctionFactoryOptions ToolOptions(string name) => new()
        {
            Name = name, MarshalResult = (value, _, _) => ValueTask.FromResult(value)
        };
        AIFunction[] tools =
        [
            AIFunctionFactory.Create((string relativePath) =>
            {
                relativePath.Should().Be("a.txt");
                return Result();
            }, ToolOptions("read_file")),
            AIFunctionFactory.Create(Result, ToolOptions("list_files"))
        ];
        var lease = new DirectTurnCapabilityLease([],
            tools.Select(tool => new DirectToolBinding(tool,
                new(tool.Name, ToolCallKind.Read))).ToArray(), new Dictionary<Guid, string>(), []);
        var factory = new DirectAgentChatRuntimeTests.FakeChatClientFactory(native)
        { Options = adapter.CreateChatOptions(preparation, tools) };
        var request = (DirectChatTurnRequest)
            DirectAgentChatRuntimeTests.CreateRequest(factory.Profile.Id);
        request = request with { Messages = request.Messages.Where(message => message.Role == MessageRole.User).ToArray() };
        var runtime = DirectAgentChatRuntimeTests.CreateRuntime(factory, new ProtocolResolver(lease));
        var events = new List<AgentStreamEvent>();
        await foreach (var item in runtime.StreamTurnAsync(request)) events.Add(item);
        handler.Bodies.Should().HaveCount(2);
        count.Should().Be(format == AiProviderApiFormat.OpenAIChatCompletions ? 2 : 1);
        events.OfType<ToolCallCompletedEvent>().Should().HaveCount(count)
            .And.OnlyContain(item => item.Status == ToolCallStatus.Completed);
        var terminal = events.OfType<RunCompletedEvent>().Should().ContainSingle().Subject;
        terminal.Status.Should().Be(RunCompletionStatus.Succeeded, terminal.ErrorMessage);
        events.OfType<UsageReportedEvent>().Should().ContainSingle()
            .Which.Usage.ProviderCalls.Should().Be(format == AiProviderApiFormat.AnthropicMessages ? 2 : 1,
                "Anthropic requires usage; the OpenAI final fixtures deliberately omit it");
        WriteProbe($"p1-second-request-{format}.json", handler.Bodies[1]);
        AssertSecondRequest(format, JsonDocument.Parse(handler.Bodies[1]).RootElement);
    }

    private sealed class ProtocolResolver(DirectTurnCapabilityLease lease)
        : IDirectTurnCapabilityResolver
    {
        public Task<DirectTurnCapabilityLease> ResolveAsync(
            DirectChatTurnRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(lease);
    }

    private static string FinalSse(AiProviderApiFormat format) => format switch
    {
        AiProviderApiFormat.AnthropicMessages => """
            event: message_start
            data: {"type":"message_start","message":{"id":"msg_final","type":"message","role":"assistant","model":"claude-sonnet-4-5","content":[],"stop_reason":null,"usage":{"input_tokens":5,"output_tokens":1}}}

            event: content_block_start
            data: {"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}

            event: content_block_delta
            data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"done"}}

            event: content_block_stop
            data: {"type":"content_block_stop","index":0}

            event: message_delta
            data: {"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":2}}

            event: message_stop
            data: {"type":"message_stop"}


            """,
        AiProviderApiFormat.OpenAIResponses => """
            event: response.created
            data: {"type":"response.created","response":{"id":"resp_final","object":"response","created_at":1,"model":"test-model","output":[],"status":"in_progress"}}

            event: response.output_text.delta
            data: {"type":"response.output_text.delta","item_id":"msg_final","output_index":0,"content_index":0,"delta":"done"}

            event: response.completed
            data: {"type":"response.completed","response":{"id":"resp_final","object":"response","created_at":1,"model":"test-model","status":"completed","output":[]}}


            """,
        _ => """
            data: {"id":"chat_final","object":"chat.completion.chunk","created":1,"model":"test-model","choices":[{"index":0,"delta":{"role":"assistant","content":"done"},"finish_reason":"stop"}]}

            data: [DONE]


            """
    };

    internal static void AssertSecondRequest(AiProviderApiFormat format, JsonElement body)
    {
        if (format == AiProviderApiFormat.AnthropicMessages)
        {
            var messages = body.GetProperty("messages").EnumerateArray().ToArray();
            var assistant = messages.Single(message => message.GetProperty("role").GetString() == "assistant");
            var content = assistant.GetProperty("content").EnumerateArray().ToArray();
            var thinking = content.Single(item => item.GetProperty("type").GetString() == "thinking");
            thinking.GetProperty("thinking").GetString().Should().Be("let me think");
            thinking.GetProperty("signature").GetString().Should().Be("sig-abc");
            var call = content.Single(item => item.GetProperty("type").GetString() == "tool_use");
            call.GetProperty("id").GetString().Should().Be("toolu_1");
            call.GetProperty("input").GetProperty("relativePath").GetString().Should().Be("a.txt");
            var result = messages.Last().GetProperty("content")[0];
            result.GetProperty("type").GetString().Should().Be("tool_result");
            result.GetProperty("tool_use_id").GetString().Should().Be("toolu_1");
            result.GetProperty("content").ToString().Should().Contain("fixture-result");
        }
        else if (format == AiProviderApiFormat.OpenAIChatCompletions)
        {
            var messages = body.GetProperty("messages").EnumerateArray().ToArray();
            var assistant = messages.Single(message => message.GetProperty("role").GetString() == "assistant");
            assistant.GetProperty("reasoning_content").GetString().Should().Be("think more");
            var calls = assistant.GetProperty("tool_calls").EnumerateArray().ToArray();
            calls.Select(call => call.GetProperty("id").GetString()).Should().Equal("call_a", "call_b");
            calls[0].GetProperty("type").GetString().Should().Be("function");
            var function = calls[0].GetProperty("function");
            function.GetProperty("name").GetString().Should().Be("read_file");
            JsonDocument.Parse(function.GetProperty("arguments").GetString()!).RootElement
                .GetProperty("relativePath").GetString().Should().Be("a.txt");
            var results = messages.Where(message => message.GetProperty("role").GetString() == "tool").ToArray();
            results.Select(result => result.GetProperty("tool_call_id").GetString()).Should().Equal("call_a", "call_b");
            results.Should().OnlyContain(result => result.GetProperty("content").ToString().Contains("fixture-result"));
        }
        else
        {
            var input = body.GetProperty("input").EnumerateArray().ToArray();
            var reasoning = input.Single(item => item.TryGetProperty("type", out var type) && type.GetString() == "reasoning");
            reasoning.GetProperty("id").GetString().Should().Be("rs_1");
            reasoning.GetProperty("encrypted_content").GetString().Should().Be("encrypted-fixture");
            reasoning.GetProperty("summary")[0].GetProperty("text").GetString().Should().Be("think ");
            var call = input.Single(item => item.TryGetProperty("type", out var type) && type.GetString() == "function_call");
            call.GetProperty("id").GetString().Should().Be("fc_1");
            call.GetProperty("status").GetString().Should().Be("completed");
            call.GetProperty("call_id").GetString().Should().Be("call_a");
            call.GetProperty("name").GetString().Should().Be("read_file");
            JsonDocument.Parse(call.GetProperty("arguments").GetString()!).RootElement
                .GetProperty("relativePath").GetString().Should().Be("a.txt");
            var result = input.Single(item => item.TryGetProperty("type", out var type) && type.GetString() == "function_call_output");
            result.GetProperty("call_id").GetString().Should().Be("call_a");
            result.GetProperty("output").ToString().Should().Contain("fixture-result");
        }
    }

    private sealed class CapturingHandler(string sse, string? final = null) : HttpMessageHandler
    {
        internal List<string> Bodies { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(Bodies.Count == 1 ? sse : final ?? sse, Encoding.UTF8, "text/event-stream") };
        }
    }

    private static string BuildChatCompletionsSse()
    {
        var builder = new StringBuilder();
        builder.Append("data: {\"id\":\"chatcmpl-1\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"test-model\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"reasoning_content\":\"think \"},\"finish_reason\":null}]}\n\n");
        builder.Append("data: {\"id\":\"chatcmpl-1\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"test-model\",\"choices\":[{\"index\":0,\"delta\":{\"reasoning_content\":\"more\"},\"finish_reason\":null}]}\n\n");
        builder.Append("data: {\"id\":\"chatcmpl-1\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"test-model\",\"choices\":[{\"index\":0,\"delta\":{\"tool_calls\":[{\"index\":0,\"id\":\"call_a\",\"type\":\"function\",\"function\":{\"name\":\"read_file\",\"arguments\":\"{\\\"relativePath\\\":\\\"\"}}]},\"finish_reason\":null}]}\n\n");
        builder.Append("data: {\"id\":\"chatcmpl-1\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"test-model\",\"choices\":[{\"index\":0,\"delta\":{\"tool_calls\":[{\"index\":0,\"function\":{\"arguments\":\"a.txt\\\"}\"}},{\"index\":1,\"id\":\"call_b\",\"type\":\"function\",\"function\":{\"name\":\"list_files\",\"arguments\":\"{}\"}}]},\"finish_reason\":null}]}\n\n");
        builder.Append("data: {\"id\":\"chatcmpl-1\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"test-model\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"tool_calls\"}]}\n\n");
        builder.Append("data: {\"id\":\"chatcmpl-1\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"test-model\",\"choices\":[],\"usage\":{\"prompt_tokens\":11,\"completion_tokens\":7,\"total_tokens\":18,\"completion_tokens_details\":{\"reasoning_tokens\":3}}}\n\n");
        builder.Append("data: [DONE]\n\n");
        return builder.ToString();
    }

    private static string BuildAnthropicSse()
    {
        var builder = new StringBuilder();
        builder.Append("event: message_start\ndata: {\"type\":\"message_start\",\"message\":{\"id\":\"msg_1\",\"type\":\"message\",\"role\":\"assistant\",\"model\":\"claude-sonnet-4-5\",\"content\":[],\"stop_reason\":null,\"stop_sequence\":null,\"usage\":{\"input_tokens\":10,\"output_tokens\":1,\"cache_creation_input_tokens\":0,\"cache_read_input_tokens\":0}}}\n\n");
        builder.Append("event: content_block_start\ndata: {\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"thinking\",\"thinking\":\"\",\"signature\":\"\"}}\n\n");
        builder.Append("event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"thinking_delta\",\"thinking\":\"let me think\"}}\n\n");
        builder.Append("event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"signature_delta\",\"signature\":\"sig-abc\"}}\n\n");
        builder.Append("event: content_block_stop\ndata: {\"type\":\"content_block_stop\",\"index\":0}\n\n");
        builder.Append("event: content_block_start\ndata: {\"type\":\"content_block_start\",\"index\":1,\"content_block\":{\"type\":\"tool_use\",\"id\":\"toolu_1\",\"name\":\"read_file\",\"input\":{}}}\n\n");
        builder.Append("event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":1,\"delta\":{\"type\":\"input_json_delta\",\"partial_json\":\"{\\\"relativePath\\\":\"}}\n\n");
        builder.Append("event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":1,\"delta\":{\"type\":\"input_json_delta\",\"partial_json\":\"\\\"a.txt\\\"}\"}}\n\n");
        builder.Append("event: content_block_stop\ndata: {\"type\":\"content_block_stop\",\"index\":1}\n\n");
        builder.Append("event: message_delta\ndata: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"tool_use\",\"stop_sequence\":null},\"usage\":{\"output_tokens\":15}}\n\n");
        builder.Append("event: message_stop\ndata: {\"type\":\"message_stop\"}\n\n");
        return builder.ToString();
    }

    private static string BuildResponsesSse()
    {
        var builder = new StringBuilder();
        builder.Append("event: response.created\ndata: {\"type\":\"response.created\",\"response\":{\"id\":\"resp_1\",\"object\":\"response\",\"created_at\":1,\"model\":\"test-model\",\"output\":[],\"status\":\"in_progress\"}}\n\n");
        builder.Append("event: response.output_item.added\ndata: {\"type\":\"response.output_item.added\",\"output_index\":0,\"item\":{\"id\":\"rs_1\",\"type\":\"reasoning\",\"encrypted_content\":\"encrypted-fixture\",\"summary\":[]}}\n\n");
        builder.Append("event: response.reasoning_summary_text.delta\ndata: {\"type\":\"response.reasoning_summary_text.delta\",\"item_id\":\"rs_1\",\"output_index\":0,\"summary_index\":0,\"delta\":\"think \"}\n\n");
        builder.Append("event: response.output_item.done\ndata: {\"type\":\"response.output_item.done\",\"output_index\":0,\"item\":{\"id\":\"rs_1\",\"type\":\"reasoning\",\"encrypted_content\":\"encrypted-fixture\",\"summary\":[{\"type\":\"summary_text\",\"text\":\"think \"}]}}\n\n");
        builder.Append("event: response.output_item.added\ndata: {\"type\":\"response.output_item.added\",\"output_index\":1,\"item\":{\"id\":\"fc_1\",\"type\":\"function_call\",\"status\":\"in_progress\",\"arguments\":\"\",\"call_id\":\"call_a\",\"name\":\"read_file\"}}\n\n");
        builder.Append("event: response.function_call_arguments.delta\ndata: {\"type\":\"response.function_call_arguments.delta\",\"item_id\":\"fc_1\",\"output_index\":1,\"delta\":\"{\\\"relativePath\\\":\"}\n\n");
        builder.Append("event: response.function_call_arguments.delta\ndata: {\"type\":\"response.function_call_arguments.delta\",\"item_id\":\"fc_1\",\"output_index\":1,\"delta\":\"\\\"a.txt\\\"}\"}\n\n");
        builder.Append("event: response.function_call_arguments.done\ndata: {\"type\":\"response.function_call_arguments.done\",\"item_id\":\"fc_1\",\"output_index\":1,\"arguments\":\"{\\\"relativePath\\\":\\\"a.txt\\\"}\"}\n\n");
        builder.Append("event: response.output_item.done\ndata: {\"type\":\"response.output_item.done\",\"output_index\":1,\"item\":{\"id\":\"fc_1\",\"type\":\"function_call\",\"status\":\"completed\",\"arguments\":\"{\\\"relativePath\\\":\\\"a.txt\\\"}\",\"call_id\":\"call_a\",\"name\":\"read_file\"}}\n\n");
        builder.Append("event: response.completed\ndata: {\"type\":\"response.completed\",\"response\":{\"id\":\"resp_1\",\"object\":\"response\",\"created_at\":1,\"model\":\"test-model\",\"status\":\"completed\",\"output\":[{\"id\":\"rs_1\",\"type\":\"reasoning\",\"encrypted_content\":\"encrypted-fixture\",\"summary\":[{\"type\":\"summary_text\",\"text\":\"think \"}]},{\"id\":\"fc_1\",\"type\":\"function_call\",\"status\":\"completed\",\"arguments\":\"{\\\"relativePath\\\":\\\"a.txt\\\"}\",\"call_id\":\"call_a\",\"name\":\"read_file\"}],\"usage\":{\"input_tokens\":11,\"output_tokens\":7,\"total_tokens\":18,\"output_tokens_details\":{\"reasoning_tokens\":3}}}}\n\n");
        return builder.ToString();
    }

    private static string BuildOllamaNdjson()
    {
        var builder = new StringBuilder();
        builder.Append("{\"model\":\"llama3.2:latest\",\"created_at\":\"2024-01-01T00:00:00Z\",\"message\":{\"role\":\"assistant\",\"content\":\"\",\"thinking\":\"think \"},\"done\":false}\n");
        builder.Append("{\"model\":\"llama3.2:latest\",\"created_at\":\"2024-01-01T00:00:00Z\",\"message\":{\"role\":\"assistant\",\"content\":\"\",\"tool_calls\":[{\"function\":{\"name\":\"read_file\",\"arguments\":{\"relativePath\":\"a.txt\"}}}]},\"done\":false}\n");
        builder.Append("{\"model\":\"llama3.2:latest\",\"created_at\":\"2024-01-01T00:00:00Z\",\"message\":{\"role\":\"assistant\",\"content\":\"\"},\"done\":true,\"done_reason\":\"stop\",\"prompt_eval_count\":11,\"eval_count\":7}\n");
        return builder.ToString();
    }

    private static string DescribeUpdates(IReadOnlyList<ChatResponseUpdate> updates)
    {
        var builder = new StringBuilder();
        for (var index = 0; index < updates.Count; index++)
        {
            var update = updates[index];
            builder.AppendLine($"--- update[{index}] messageId={update.MessageId ?? "<null>"} responseId={update.ResponseId ?? "<null>"} role={update.Role} finish={update.FinishReason?.ToString() ?? "<null>"}");
            foreach (var content in update.Contents)
            {
                builder.AppendLine($"    {DescribeContent(content)}");
            }

            if (update.AdditionalProperties is { Count: > 0 } properties)
            {
                builder.AppendLine($"    additional={JsonSerializer.Serialize(properties)}");
            }
        }

        return builder.ToString();
    }

    private static string DescribeResponse(ChatResponse response)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"message.role={response.Messages[0].Role} id={response.Messages[0].MessageId ?? "<null>"}");
        foreach (var content in response.Messages[0].Contents)
        {
            builder.AppendLine($"    {DescribeContent(content)}");
        }

        builder.AppendLine($"finish={response.FinishReason?.ToString() ?? "<null>"}");
        builder.AppendLine($"usage={DescribeUsage(response.Usage)}");
        return builder.ToString();
    }

    private static string DescribeContent(AIContent content)
        => content switch
        {
            FunctionCallContent call =>
                $"FunctionCall id={call.CallId} name={call.Name} args={JsonSerializer.Serialize(call.Arguments)}",
            FunctionResultContent result =>
                $"FunctionResult id={result.CallId} result={JsonSerializer.Serialize(result.Result)}",
            TextReasoningContent reasoning =>
                $"Reasoning text={JsonSerializer.Serialize(reasoning.Text)} protected={JsonSerializer.Serialize(GetProtectedData(reasoning))}",
            TextContent text => $"Text {JsonSerializer.Serialize(text.Text)}",
            UsageContent usage => $"Usage {DescribeUsage(usage.Details)}",
            _ => content.GetType().Name
        };

    private static string DescribeUsage(UsageDetails? usage)
        => usage is null
            ? "<null>"
            : JsonSerializer.Serialize(new
            {
                usage.InputTokenCount,
                usage.OutputTokenCount,
                usage.CachedInputTokenCount,
                usage.ReasoningTokenCount,
                usage.TotalTokenCount,
                AdditionalCounts = usage.AdditionalCounts is { Count: > 0 } counts
                    ? counts.ToDictionary(pair => pair.Key, pair => pair.Value)
                    : null
            });

    private static object? GetProtectedData(TextReasoningContent reasoning)
    {
        var property = typeof(TextReasoningContent).GetProperty("ProtectedData");
        return property?.GetValue(reasoning);
    }

    private static void WriteProbe(string fileName, string content)
        => File.WriteAllText(Path.Combine(ProbeDirectory, fileName), content);

    private static AiProviderClientRequest CreateOpenAiRequest(AiProviderApiFormat apiFormat)
    {
        var now = DateTimeOffset.UtcNow;
        var connection = new AiProviderConnection(
            Guid.NewGuid(), "openai", "OpenAI", AiProviderKind.OpenAI,
            new Uri("https://api.example.test/v1/"), AiProviderAuthKind.ApiKey,
            new Dictionary<string, string> { [OpenAiProviderAdapter.ApiKeySecretName] = "secret:openai" },
            JsonObject("{}"), now, now);
        var profile = new AiModelProfile(
            Guid.NewGuid(), connection.Id, "Test", apiFormat, "test-model",
            new AiSamplingOptions(false, 0.7, false, 0.7), JsonObject("{}"), now, now);
        return new AiProviderClientRequest(
            connection, profile,
            new Dictionary<string, string> { [OpenAiProviderAdapter.ApiKeySecretName] = "test-key" }, false);
    }

    private static AiProviderClientRequest CreateOllamaRequest()
    {
        var now = DateTimeOffset.UtcNow;
        var connection = new AiProviderConnection(
            Guid.NewGuid(), "ollama", "Local Ollama", AiProviderKind.Ollama,
            new Uri("http://localhost:11434/"), AiProviderAuthKind.None,
            new Dictionary<string, string>(), JsonObject("{}"), now, now);
        var profile = new AiModelProfile(
            Guid.NewGuid(), connection.Id, "Test", AiProviderApiFormat.OllamaNative, "llama3.2:latest",
            new AiSamplingOptions(false, 0.7, false, 0.7), JsonObject("{}"), now, now);
        return new AiProviderClientRequest(connection, profile, new Dictionary<string, string>(), false);
    }

    private static AiProviderClientRequest CreateAnthropicRequest()
    {
        var now = DateTimeOffset.UtcNow;
        var connection = new AiProviderConnection(
            Guid.NewGuid(), "anthropic", "Anthropic", AiProviderKind.Anthropic,
            new Uri("https://api.anthropic.com/"), AiProviderAuthKind.ApiKey,
            new Dictionary<string, string> { [AnthropicProviderAdapter.ApiKeySecretName] = "secret:anthropic" },
            JsonObject("{}"), now, now);
        var profile = new AiModelProfile(
            Guid.NewGuid(), connection.Id, "Test", AiProviderApiFormat.AnthropicMessages, "claude-sonnet-4-5",
            new AiSamplingOptions(false, 0.7, false, 0.7), JsonObject("{}"), now, now);
        return new AiProviderClientRequest(
            connection, profile,
            new Dictionary<string, string> { [AnthropicProviderAdapter.ApiKeySecretName] = "test-key" }, false);
    }

    private static IReadOnlyDictionary<string, JsonElement> JsonObject(string json)
        => JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json) ?? [];

    private sealed class SseHandler(string sse, string contentType = "text/event-stream") : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(sse, Encoding.UTF8)
            };
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
            return Task.FromResult(response);
        }
    }
}
