using System.Runtime.CompilerServices;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.AI;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Core.Runtime.Agent;
using SelfClaw.Infrastructure.Agents.Runtime;
using SelfClaw.Infrastructure.AiProviders.Abstractions;
using SelfClaw.Infrastructure.AiProviders.Models;

namespace SelfClaw.Tests.Infrastructure.Agents.Runtime;

public sealed class DirectAgentChatRuntimeTests
{
    [Fact]
    public async Task StreamTurnAsync_translates_all_content_and_aggregates_usage()
    {
        var client = new ScriptedChatClient(
        [
            Update("message-1",
                new TextReasoningContent("thinking"),
                new TextContent("Hello "),
                new FunctionCallContent("call-1", "read_file", new Dictionary<string, object?>
                {
                    ["relativePath"] = "README.md"
                }),
                new FunctionCallContent("call-1", "read_file", new Dictionary<string, object?>()),
                new FunctionResultContent("call-1", new WorkspaceFileContent("README.md", "body", false)),
                new UsageContent(new UsageDetails { InputTokenCount = 10, OutputTokenCount = 2 })),
            Update("message-1",
                new TextContent("world"),
                new UsageContent(new UsageDetails { InputTokenCount = 3, OutputTokenCount = 4 }))
        ]);
        var factory = new FakeChatClientFactory(client);
        var runtime = CreateRuntime(factory);
        var request = CreateRequest(modelProfileId: factory.Profile.Id);

        var events = await CollectAsync(runtime.StreamTurnAsync(request));

        events[0].Should().BeOfType<RunStartedEvent>().Which.Should().Match<RunStartedEvent>(started =>
            started.SessionId!.StartsWith("direct-", StringComparison.Ordinal) &&
            started.Model == "test-model" &&
            started.AgentKind == null);
        events[1].Should().Be(new RunStatusEvent(AgentRunStatus.Requesting));
        events.OfType<AssistantThinkingDeltaEvent>().Should().ContainSingle()
            .Which.Should().Be(new AssistantThinkingDeltaEvent("message-1", "thinking"));
        events.OfType<AssistantTextDeltaEvent>().Select(item => item.Delta).Should().Equal("Hello ", "world");
        events.OfType<ToolCallStartedEvent>().Should().ContainSingle().Which.Kind.Should().Be(ToolCallKind.Read);
        events.OfType<ToolCallCompletedEvent>().Should().ContainSingle().Which.Should().Match<ToolCallCompletedEvent>(item =>
            item.ToolCallId == "call-1" && item.Status == ToolCallStatus.Completed &&
            item.ResultSummary == "Read README.md." && item.ResultContent == "body");
        events.OfType<UsageReportedEvent>().Should().ContainSingle().Which
            .Should().Be(new UsageReportedEvent(13, 6));
        events.Last().Should().Be(new RunCompletedEvent(RunCompletionStatus.Succeeded, "Hello world", null));
        events.OfType<RunCompletedEvent>().Should().ContainSingle();
        client.IsDisposed.Should().BeTrue();

        client.LastMessages.Select(message => message.Role).Should().Equal(
            ChatRole.System,
            ChatRole.User,
            ChatRole.Assistant);
        client.LastMessages.Select(message => message.Text).Should().Equal(
            "Follow project instructions.",
            "user prompt",
            "prior answer");
        factory.LastInputs!.Tools.Should().BeEmpty();
    }

    [Fact]
    public async Task StreamTurnAsync_uses_scope_default_and_binds_workspace_tools()
    {
        var client = new ScriptedChatClient([]);
        var factory = new FakeChatClientFactory(client);
        var runtime = CreateRuntime(factory);
        var request = CreateRequest(modelProfileId: null, workspace: CreateWorkspace());

        var events = await CollectAsync(runtime.StreamTurnAsync(request));

        factory.ScopeCalls.Should().Equal(AiModelSelectionScopes.DesktopDefault);
        factory.LastInputs!.Tools.Select(tool => tool.Name).Should().Equal(
            "list_files", "search_text", "read_file", "write_file", "run_shell_command");
        events.Last().Should().Be(new RunCompletedEvent(RunCompletionStatus.Succeeded, "", null));
    }

    [Fact]
    public async Task StreamTurnAsync_propagates_cancellation_and_disposes_client()
    {
        var client = new ScriptedChatClient(
            [Update("m", new TextContent("partial"))],
            new OperationCanceledException());
        var runtime = CreateRuntime(new FakeChatClientFactory(client));

        var action = () => CollectAsync(runtime.StreamTurnAsync(CreateRequest(Guid.NewGuid())));

        await action.Should().ThrowAsync<OperationCanceledException>();
        client.IsDisposed.Should().BeTrue();
    }

    [Fact]
    public async Task StreamTurnAsync_converts_setup_and_stream_failures_without_throwing()
    {
        var setupFactory = new FakeChatClientFactory(new ScriptedChatClient([]))
        {
            FactoryException = new InvalidOperationException("default model missing")
        };
        var setupEvents = await CollectAsync(CreateRuntime(setupFactory).StreamTurnAsync(CreateRequest(null)));
        setupEvents.Should().ContainSingle().Which.Should().Be(
            new RunCompletedEvent(RunCompletionStatus.Failed, null, "default model missing"));

        var client = new ScriptedChatClient(
            [Update("m", new TextContent("partial"))],
            new HttpRequestException("provider failed"));
        var streamEvents = await CollectAsync(
            CreateRuntime(new FakeChatClientFactory(client)).StreamTurnAsync(CreateRequest(Guid.NewGuid())));
        streamEvents.OfType<RunCompletedEvent>().Should().ContainSingle().Which.Should().Be(
            new RunCompletedEvent(RunCompletionStatus.Failed, "partial", "provider failed"));
        client.IsDisposed.Should().BeTrue();
    }

    private static DirectAgentChatRuntime CreateRuntime(FakeChatClientFactory factory)
        => new(factory, new WorkspaceAgentToolset(new NoOpWorkspaceTools()));

    private static ChatTurnRequest CreateRequest(Guid? modelProfileId, WorkspaceRoot? workspace = null)
    {
        var conversationId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        MessageRecord Message(MessageRole role, string text, MessageStatus status = MessageStatus.Completed)
            => new(Guid.NewGuid(), conversationId, role, text, status, now, now);

        return new DirectChatTurnRequest(
            conversationId,
            workspace,
            new AgentRuntimeDefinition(
                "direct-test", "Direct", "test", AgentExecutionMode.Direct,
                AgentRuntimeDefinition.SystemToolPolicy, [], [], [], "Follow project instructions."),
            [
                Message(MessageRole.System, "ignored system history"),
                Message(MessageRole.User, "user prompt"),
                Message(MessageRole.Assistant, "failed answer", MessageStatus.Failed),
                Message(MessageRole.Assistant, "cancelled answer", MessageStatus.Cancelled),
                Message(MessageRole.Assistant, "prior answer")
            ],
            modelProfileId,
            ToolPermissionMode.FullAccess,
            ToolApprovalHandler: null);
    }

    private static WorkspaceRoot CreateWorkspace()
    {
        var now = DateTimeOffset.UtcNow;
        return new WorkspaceRoot(Guid.NewGuid(), "Repo", "E:\\repo", now, now);
    }

    private static ChatResponseUpdate Update(string messageId, params AIContent[] contents)
        => new(ChatRole.Assistant, contents) { MessageId = messageId };

    private static async Task<List<AgentStreamEvent>> CollectAsync(IAsyncEnumerable<AgentStreamEvent> events)
    {
        var result = new List<AgentStreamEvent>();
        await foreach (var item in events)
        {
            result.Add(item);
        }

        return result;
    }

    private sealed class FakeChatClientFactory : IAiChatClientFactory
    {
        private readonly IChatClient _client;

        public FakeChatClientFactory(IChatClient client)
        {
            _client = client;
            var now = DateTimeOffset.UtcNow;
            Profile = new AiModelProfile(
                Guid.NewGuid(), Guid.NewGuid(), "Test", AiProviderApiFormat.OpenAIChatCompletions,
                "test-model", new AiSamplingOptions(false, 0, false, 0),
                new Dictionary<string, JsonElement>(), now, now);
        }

        public AiModelProfile Profile { get; }
        public Exception? FactoryException { get; init; }
        public AiChatRuntimeInputs? LastInputs { get; private set; }
        public List<string> ScopeCalls { get; } = [];

        public Task<AiChatClientLease> CreateAsync(
            Guid modelProfileId,
            AiChatRuntimeInputs inputs,
            CancellationToken cancellationToken = default)
        {
            LastInputs = inputs;
            return CreateLease();
        }

        public Task<AiChatClientLease> CreateForScopeAsync(
            string scope,
            AiChatRuntimeInputs inputs,
            CancellationToken cancellationToken = default)
        {
            ScopeCalls.Add(scope);
            LastInputs = inputs;
            return CreateLease();
        }

        private Task<AiChatClientLease> CreateLease()
            => FactoryException is null
                ? Task.FromResult(new AiChatClientLease(_client, new ChatOptions(), Profile))
                : Task.FromException<AiChatClientLease>(FactoryException);
    }

    private sealed class ScriptedChatClient : IChatClient
    {
        private readonly IReadOnlyList<ChatResponseUpdate> _updates;
        private readonly Exception? _terminalException;

        public ScriptedChatClient(IReadOnlyList<ChatResponseUpdate> updates, Exception? terminalException = null)
        {
            _updates = updates;
            _terminalException = terminalException;
        }

        public List<ChatMessage> LastMessages { get; } = [];
        public bool IsDisposed { get; private set; }

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
            LastMessages.AddRange(messages);
            foreach (var update in _updates)
            {
                yield return update;
            }

            if (_terminalException is not null)
            {
                throw _terminalException;
            }

            await Task.CompletedTask;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() => IsDisposed = true;
    }

    private sealed class NoOpWorkspaceTools : IWorkspaceToolService
    {
        public Task<IReadOnlyList<WorkspaceFileEntry>> ListFilesAsync(string root, string? path, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<WorkspaceFileEntry>>([]);
        public Task<IReadOnlyList<WorkspaceSearchHit>> SearchTextAsync(string root, string query, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<WorkspaceSearchHit>>([]);
        public Task<WorkspaceFileContent> ReadFileAsync(string root, string path, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<WorkspaceFileWriteResult> WriteFileAsync(string root, string path, string content, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ShellCommandResult> RunShellCommandAsync(string root, string command, int timeout, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
