using System.Runtime.CompilerServices;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Runtime;
using SelfClaw.Infrastructure.Agents.Conversations;
using SelfClaw.Tests.Infrastructure.Agents.Direct;

namespace SelfClaw.Tests.Infrastructure.Agents.Conversations;

public sealed class ConversationTitleGeneratorTests
{
    [Fact]
    public async Task Sends_the_instruction_and_the_prompt_and_leaves_sampling_to_the_provider()
    {
        var client = new RecordingChatClient((_, _, _) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Fix login redirect"))));
        var factory = new DirectAgentChatRuntimeTests.FakeChatClientFactory(client);

        var title = await new ConversationTitleGenerator(factory).GenerateTitleAsync("the login keeps redirecting", null);

        title.Should().Be("Fix login redirect");
        factory.CreateCalls.Should().Be(1);
        factory.LastPipeline.Should().NotBeNull();
        factory.LastPipeline!.Tools.Should().BeEmpty("the title call must not carry turn tools");
        factory.ScopeCalls.Should().ContainSingle().Which.Should().Be(AiModelSelectionScopes.DesktopDefault,
            "a null model profile resolves the desktop default");
        client.IsDisposed.Should().BeTrue("the lease is released inside the generator");

        var request = client.Requests.Should().ContainSingle().Which;
        request.Messages.Should().HaveCount(2);
        request.Messages[0].Role.Should().Be(ChatRole.System);
        request.Messages[0].Text.Should().Contain("first user message").And.Contain("48 characters");
        request.Messages[1].Role.Should().Be(ChatRole.User);
        request.Messages[1].Text.Should().Be("the login keeps redirecting");

        request.Options.Should().NotBeNull();
        var options = request.Options!;
        options.MaxOutputTokens.Should().Be(1_024,
            "the same budget pays for a reasoning model's thinking, which the provider counts against the answer");
        options.Temperature.Should().BeNull("sampling stays at the provider's default");
        options.TopP.Should().BeNull();
        options.Tools.Should().BeNull();
        options.ToolMode.Should().BeNull();
        options.RawRepresentationFactory.Should().BeNull(
            "the profile's reasoning effort (思考等级) must not be reconstructed for this call");
    }

    [Fact]
    public async Task Uses_the_requested_model_profile_without_resolving_a_default()
    {
        var client = new RecordingChatClient();
        var factory = new DirectAgentChatRuntimeTests.FakeChatClientFactory(client);

        await new ConversationTitleGenerator(factory).GenerateTitleAsync("hello", factory.Profile.Id);

        factory.ScopeCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task Truncates_a_long_prompt_and_caps_the_returned_title()
    {
        var client = new RecordingChatClient((_, _, _) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, new string('x', 100)))));
        var prompt = new string('p', 5_000);

        var title = await new ConversationTitleGenerator(new DirectAgentChatRuntimeTests.FakeChatClientFactory(client))
            .GenerateTitleAsync(prompt, null);

        client.Requests.Single().Messages[1].Text.Should().HaveLength(4_000);
        title.Should().HaveLength(48);
    }

    [Theory]
    [InlineData("\"Hello\nWorld\"", "Hello World")]
    [InlineData("「修复登录跳转」", "修复登录跳转")]
    [InlineData("  spaced   out  ", "spaced out")]
    public async Task Sanitizes_quotes_line_breaks_and_extra_whitespace(string responseText, string expected)
    {
        var client = new RecordingChatClient((_, _, _) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, responseText))));

        var title = await new ConversationTitleGenerator(new DirectAgentChatRuntimeTests.FakeChatClientFactory(client))
            .GenerateTitleAsync("hello", null);

        title.Should().Be(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n\t ")]
    [InlineData("\"\"")]
    public async Task Returns_null_for_an_empty_response(string responseText)
    {
        var client = new RecordingChatClient((_, _, _) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, responseText))));

        var title = await new ConversationTitleGenerator(new DirectAgentChatRuntimeTests.FakeChatClientFactory(client))
            .GenerateTitleAsync("hello", null);

        title.Should().BeNull();
    }

    [Fact]
    public async Task Returns_null_and_warns_when_the_model_answers_no_text()
    {
        // A thinking model that spends the whole output budget before its first title character answers
        // finish_reason=length with no text at all (2026-11-09): the only way to tell that apart from a
        // model that simply said nothing is to log the finish reason and the budget it ran against.
        var client = new RecordingChatClient((_, _, _) => Task.FromResult(
            new ChatResponse(new ChatMessage(ChatRole.Assistant, string.Empty))
            {
                FinishReason = ChatFinishReason.Length
            }));
        var logger = new CapturingLogger();

        var title = await new ConversationTitleGenerator(
            new DirectAgentChatRuntimeTests.FakeChatClientFactory(client), logger)
            .GenerateTitleAsync("hello", null);

        title.Should().BeNull();
        var warning = logger.Messages.Should().ContainSingle().Subject;
        warning.Should().Contain("no usable text").And.Contain("Budget=1024");
    }

    [Fact]
    public async Task Returns_null_and_warns_when_the_provider_fails()
    {
        var client = new RecordingChatClient((_, _, _) =>
            Task.FromException<ChatResponse>(new InvalidOperationException("provider exploded")));
        var logger = new CapturingLogger();

        var title = await new ConversationTitleGenerator(
            new DirectAgentChatRuntimeTests.FakeChatClientFactory(client), logger)
            .GenerateTitleAsync("secret prompt text", null);

        title.Should().BeNull();
        var warning = logger.Messages.Should().ContainSingle().Subject;
        warning.Should().NotContain("secret prompt text", "diagnostics must not echo prompt content");
    }

    [Fact]
    public async Task Returns_null_and_warns_when_the_model_cannot_be_prepared()
    {
        var client = new RecordingChatClient();
        var factory = new DirectAgentChatRuntimeTests.FakeChatClientFactory(client)
        {
            PreparationException = new InvalidOperationException("the model profile is disabled")
        };
        var logger = new CapturingLogger();

        var title = await new ConversationTitleGenerator(factory, logger).GenerateTitleAsync("hello", null);

        title.Should().BeNull();
        factory.CreateCalls.Should().Be(0);
        logger.Messages.Should().ContainSingle();
    }

    [Fact]
    public async Task Rethrows_cancellation()
    {
        var client = new RecordingChatClient((_, _, _) =>
        {
            var source = new CancellationTokenSource();
            source.Cancel();
            return Task.FromCanceled<ChatResponse>(source.Token);
        });

        var action = () => new ConversationTitleGenerator(new DirectAgentChatRuntimeTests.FakeChatClientFactory(client))
            .GenerateTitleAsync("hello", null);

        await action.Should().ThrowAsync<OperationCanceledException>();
    }

    private sealed class RecordingChatClient : IChatClient
    {
        private readonly Func<IReadOnlyList<ChatMessage>, ChatOptions?, CancellationToken, Task<ChatResponse>> _response;

        internal RecordingChatClient(
            Func<IReadOnlyList<ChatMessage>, ChatOptions?, CancellationToken, Task<ChatResponse>>? response = null)
            => _response = response ?? ((_, _, _) =>
                Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Titled"))));

        internal List<Request> Requests { get; } = [];

        internal bool IsDisposed { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var captured = messages.ToList();
            Requests.Add(new Request(captured, options));
            var response = await _response(captured, options, cancellationToken).ConfigureAwait(false);
            foreach (var message in response.Messages)
            {
                yield return new ChatResponseUpdate(message.Role, message.Contents);
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() => IsDisposed = true;

        internal sealed record Request(IReadOnlyList<ChatMessage> Messages, ChatOptions? Options);
    }

    private sealed class CapturingLogger : ILogger<ConversationTitleGenerator>
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
