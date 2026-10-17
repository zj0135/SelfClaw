using System.Runtime.CompilerServices;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using SelfClaw.Core.Models;
using SelfClaw.Infrastructure.Agents.Direct.Abstractions;
using SelfClaw.Infrastructure.Agents.Direct.Context;
using SelfClaw.Infrastructure.Agents.Direct.Context.Models;
using SelfClaw.Infrastructure.AiProviders;
using SelfClaw.Infrastructure.AiProviders.Models;
using SelfClaw.Tests.Infrastructure.Agents.Direct;
using SelfClaw.Tests.TestDoubles;
using Xunit.Abstractions;

namespace SelfClaw.Tests.Infrastructure.Agents.Direct.Context;

public sealed class DirectContextCompressorTests
{
    private static readonly Guid ConversationId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid TurnId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private readonly ITestOutputHelper _output;

    public DirectContextCompressorTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task Sends_prefix_unit_messages_verbatim_with_roles_and_tool_calls()
    {
        var client = new RecordingChatClient();
        var counter = new DirectTokenCounter();
        var compressor = CreateCompressor(new RecordingChatClient(), tokenCounter: counter);
        var turnProfile = CreateProfile("turn-model");
        var call = new FunctionCallContent("call-1", "read_file", new Dictionary<string, object?> { ["path"] = "a.cs" });
        var result = new FunctionResultContent("call-1", "file content");
        var units = new[]
        {
            Unit(new ChatMessage(ChatRole.User, "list the files")),
            Unit(new ChatMessage(ChatRole.Assistant, [call]), new ChatMessage(ChatRole.Tool, [result]))
        };

        var outcome = await compressor.TryCompressAsync(Request(units, turnLease: CreateLease(client, turnProfile)));

        var sent = client.Requests.Single().Messages;
        sent.Should().HaveCount(4);
        sent[0].Role.Should().Be(ChatRole.System);
        sent[0].Text.Should().Be(CompressionInstruction.Text);
        sent[1].Should().BeSameAs(units[0].Messages[0]);
        sent[2].Should().BeSameAs(units[1].Messages[0]);
        sent[3].Should().BeSameAs(units[1].Messages[1]);
        sent[2].Contents.Single().Should().BeOfType<FunctionCallContent>().Which.Name.Should().Be("read_file");
        sent[3].Contents.Single().Should().BeOfType<FunctionResultContent>().Which.CallId.Should().Be("call-1");

        outcome.Should().NotBeNull();
        outcome.Summary.Should().Be("summarized history");
        outcome.Model.Should().Be("turn-model");
        outcome.UsedFallbackModel.Should().BeFalse();
        outcome.SourceMessageCount.Should().Be(2);
        outcome.SourceTokens.Should().Be(1200);
        outcome.SummaryTokens.Should().Be(counter.CountText("summarized history", turnProfile));
    }

    [Fact]
    public async Task Source_message_count_counts_units_not_expanded_chat_messages()
    {
        var client = new RecordingChatClient();
        var call = new FunctionCallContent("call-1", "read_file", new Dictionary<string, object?> { ["path"] = "a.cs" });
        var units = new[]
        {
            Unit(new ChatMessage(ChatRole.User, "list the files")),
            Unit(
                new ChatMessage(ChatRole.Assistant, [call]),
                new ChatMessage(ChatRole.Tool, [new FunctionResultContent("call-1", "file content")]))
        };

        var outcome = await CreateCompressor(client).TryCompressAsync(Request(
            units,
            turnLease: CreateLease(client, "turn-model")));

        // One unit is one stored conversation message, and the transcript's "compressed N messages"
        // counts those messages, so the tool round trip inside the second unit must not add one.
        outcome.Should().NotBeNull();
        outcome.SourceMessageCount.Should().Be(2);
    }

    [Fact]
    public async Task Uses_a_dedicated_lease_for_the_requested_model_and_disposes_it()
    {
        var turnClient = new RecordingChatClient();
        var dedicatedClient = new RecordingChatClient();
        var factory = new DirectAgentChatRuntimeTests.FakeChatClientFactory(dedicatedClient)
        {
            Profile = CreateProfile("compression-model")
        };
        var catalog = new StubAiModelCatalog
        {
            EnabledModels = [new EnabledModelView(factory.Profile.Id, "compression", "compression-model", "provider")]
        };

        var outcome = await new DirectContextCompressor(factory, catalog, new DirectTokenCounter()).TryCompressAsync(Request(
            [Unit(new ChatMessage(ChatRole.User, "hello"))],
            modelProfileId: factory.Profile.Id,
            turnLease: CreateLease(turnClient, "turn-model")));

        factory.CreateCalls.Should().Be(1);
        factory.LastPipeline.Should().NotBeNull();
        factory.LastPipeline.Tools.Should().BeEmpty("the summary call must not carry the turn's tools");
        dedicatedClient.Requests.Should().ContainSingle();
        turnClient.Requests.Should().BeEmpty();
        dedicatedClient.IsDisposed.Should().BeTrue("the dedicated lease is released inside the compressor");

        outcome.Should().NotBeNull();
        outcome.Model.Should().Be("compression-model");
        outcome.UsedFallbackModel.Should().BeFalse();
        outcome.SummaryTokens.Should().Be(new DirectTokenCounter().CountText("summarized history", factory.Profile));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Falls_back_to_the_turn_lease_when_the_requested_model_is_unusable(bool preparationThrows)
    {
        // The two profiles map to different vocabularies (6 vs 3 tokens for this text), so the summary
        // count proves which profile was used.
        const string summary = "你好，世界";
        var turnClient = new RecordingChatClient((_, _, _) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, summary))));
        var dedicatedClient = new RecordingChatClient();
        var factory = new DirectAgentChatRuntimeTests.FakeChatClientFactory(dedicatedClient)
        {
            Profile = CreateProfile("compression-model"),
            PreparationException = preparationThrows
                ? new InvalidOperationException("the model profile is disabled")
                : null
        };
        var catalog = new StubAiModelCatalog();
        Guid modelProfileId = factory.Profile.Id;
        if (preparationThrows)
        {
            catalog.EnabledModels = [new EnabledModelView(modelProfileId, "compression", "compression-model", "provider")];
        }

        var logger = new CapturingLogger();
        var outcome = await new DirectContextCompressor(factory, catalog, new DirectTokenCounter(), logger).TryCompressAsync(Request(
            [Unit(new ChatMessage(ChatRole.User, "hello"))],
            modelProfileId: modelProfileId,
            turnLease: CreateLease(turnClient, "gpt-4-turbo")));

        factory.CreateCalls.Should().Be(0);
        dedicatedClient.Requests.Should().BeEmpty();
        dedicatedClient.IsDisposed.Should().BeFalse();
        turnClient.Requests.Should().ContainSingle();

        outcome.Should().NotBeNull();
        outcome.Model.Should().Be("gpt-4-turbo");
        outcome.UsedFallbackModel.Should().BeTrue();
        outcome.Summary.Should().Be(summary);
        outcome.SummaryTokens.Should().Be(6, "the summary is counted with the turn lease's cl100k profile");
        logger.Messages.Should().ContainSingle();
    }

    [Theory]
    [InlineData(0, 512)]
    [InlineData(400, 512)]
    [InlineData(2048, 512)]
    [InlineData(8000, 2000)]
    [InlineData(100_000, 4096)]
    public async Task Clamps_the_output_budget_and_leaves_sampling_to_the_provider(int sourceTokens, int expectedMaximum)
    {
        var client = new RecordingChatClient();

        await CreateCompressor(client).TryCompressAsync(Request(
            [Unit(new ChatMessage(ChatRole.User, "hello"))],
            sourceTokens: sourceTokens,
            turnLease: CreateLease(client, "turn-model")));

        var options = client.Requests.Single().Options;
        options.Should().NotBeNull();
        options.MaxOutputTokens.Should().Be(expectedMaximum);
        options.Temperature.Should().BeNull("sampling stays at the provider's default");
        options.TopP.Should().BeNull();
        options.Tools.Should().BeNull();
        options.ToolMode.Should().BeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n\t ")]
    public async Task Returns_null_for_an_empty_response(string responseText)
    {
        var client = new RecordingChatClient((_, _, _) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, responseText))));
        var logger = new CapturingLogger();

        var outcome = await CreateCompressor(client, logger).TryCompressAsync(Request(
            [Unit(new ChatMessage(ChatRole.User, "hello"))],
            turnLease: CreateLease(client, "turn-model")));

        outcome.Should().BeNull();
        logger.Messages.Should().ContainSingle().Which.Should().Contain(ConversationId.ToString()).And.Contain(TurnId.ToString());
    }

    [Fact]
    public async Task Returns_null_and_warns_when_the_provider_fails()
    {
        var client = new RecordingChatClient((_, _, _) =>
            Task.FromException<ChatResponse>(new InvalidOperationException("provider exploded")));
        var logger = new CapturingLogger();

        var outcome = await CreateCompressor(client, logger).TryCompressAsync(Request(
            [Unit(new ChatMessage(ChatRole.User, "secret prompt text"))],
            turnLease: CreateLease(client, "turn-model")));

        outcome.Should().BeNull();
        var warning = logger.Messages.Should().ContainSingle().Subject;
        warning.Should().Contain(ConversationId.ToString()).And.Contain(TurnId.ToString());
        warning.Should().NotContain("secret prompt text", "diagnostics must not echo prompt content");
        _output.WriteLine(warning);
    }

    [Fact]
    public async Task Rethrows_cancellation()
    {
        var client = new RecordingChatClient((_, _, cancellationToken) =>
        {
            var source = new CancellationTokenSource();
            source.Cancel();
            return Task.FromCanceled<ChatResponse>(source.Token);
        });

        var action = () => CreateCompressor(client).TryCompressAsync(Request(
            [Unit(new ChatMessage(ChatRole.User, "hello"))],
            turnLease: CreateLease(client, "turn-model")));

        await action.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Prepends_the_previous_summary_in_its_wrapper()
    {
        var client = new RecordingChatClient();
        var units = new[] { Unit(new ChatMessage(ChatRole.User, "the newest message")) };

        await CreateCompressor(client).TryCompressAsync(Request(
            units,
            turnLease: CreateLease(client, "turn-model"),
            previousSummary: "earlier decisions"));

        var sent = client.Requests.Single().Messages;
        sent.Should().HaveCount(3);
        sent[1].Role.Should().Be(ChatRole.User);
        sent[1].Text.Should().Contain("<selfclaw-context-summary version=\"1\">")
            .And.Contain("earlier decisions")
            .And.Contain("</selfclaw-context-summary>");
        sent[2].Should().BeSameAs(units[0].Messages[0]);
    }

    [Fact]
    public async Task Leaves_the_summary_token_count_null_when_counting_is_unavailable()
    {
        var client = new RecordingChatClient();

        var outcome = await CreateCompressor(client, tokenCounter: new UnavailableTokenCounter()).TryCompressAsync(Request(
            [Unit(new ChatMessage(ChatRole.User, "hello"))],
            turnLease: CreateLease(client, "turn-model")));

        outcome.Should().NotBeNull();
        outcome.Summary.Should().Be("summarized history");
        outcome.SummaryTokens.Should().BeNull();
    }

    [Fact]
    public async Task Returns_null_when_no_chat_client_is_available()
    {
        var logger = new CapturingLogger();

        var outcome = await CreateCompressor(new RecordingChatClient(), logger).TryCompressAsync(Request(
            [Unit(new ChatMessage(ChatRole.User, "hello"))]));

        outcome.Should().BeNull();
        logger.Messages.Should().ContainSingle().Which.Should().Contain(ConversationId.ToString());
    }

    private static DirectContextCompressor CreateCompressor(
        RecordingChatClient client,
        CapturingLogger? logger = null,
        IDirectTokenCounter? tokenCounter = null)
        => new(
            new DirectAgentChatRuntimeTests.FakeChatClientFactory(client),
            new StubAiModelCatalog(),
            tokenCounter ?? new DirectTokenCounter(),
            logger);

    private static DirectCompressionRequest Request(
        IReadOnlyList<DirectPromptHistoryUnit> units,
        int sourceTokens = 1200,
        Guid? modelProfileId = null,
        AiChatClientLease? turnLease = null,
        string? previousSummary = null)
        => new(units, previousSummary, sourceTokens, modelProfileId, turnLease, ConversationId, TurnId);

    // The unit's sequence and token count are irrelevant here: the compressor only replays its messages.
    private static DirectPromptHistoryUnit Unit(params ChatMessage[] messages) => new(messages, 0);

    private static AiChatClientLease CreateLease(IChatClient client, string model)
        => CreateLease(client, CreateProfile(model));

    private static AiChatClientLease CreateLease(IChatClient client, AiModelProfile profile)
        => new(client, new ChatOptions(), profile, new HttpClient());

    private static AiModelProfile CreateProfile(string model)
    {
        var now = DateTimeOffset.UtcNow;
        return new AiModelProfile(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "test-model",
            AiProviderApiFormat.OpenAIChatCompletions,
            model,
            new AiSamplingOptions(false, 0d, false, 0d),
            new Dictionary<string, System.Text.Json.JsonElement>(),
            now,
            now);
    }

    private sealed class UnavailableTokenCounter : IDirectTokenCounter
    {
        public bool IsAvailable(AiModelProfile? model) => false;

        public int? CountText(string? text, AiModelProfile? model) => null;

        public int? CountMessages(IReadOnlyList<ChatMessage> messages, AiModelProfile? model) => null;

        public int? CountTools(IEnumerable<AITool>? tools, AiModelProfile? model) => null;
    }

    private sealed class RecordingChatClient : IChatClient
    {
        private readonly Func<IReadOnlyList<ChatMessage>, ChatOptions?, CancellationToken, Task<ChatResponse>> _response;

        internal RecordingChatClient(
            Func<IReadOnlyList<ChatMessage>, ChatOptions?, CancellationToken, Task<ChatResponse>>? response = null)
            => _response = response ?? ((_, _, _) =>
                Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "summarized history"))));

        internal List<Request> Requests { get; } = [];

        internal bool IsDisposed { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var captured = messages.ToList();
            Requests.Add(new Request(captured, options));
            return _response(captured, options, cancellationToken);
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() => IsDisposed = true;

        internal sealed record Request(IReadOnlyList<ChatMessage> Messages, ChatOptions? Options);
    }

    private sealed class CapturingLogger : ILogger<DirectContextCompressor>
    {
        internal List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Messages.Add(formatter(state, exception));
    }
}
