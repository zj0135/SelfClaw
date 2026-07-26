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
using SelfClaw.Infrastructure.Extensions.Abstractions;
using SelfClaw.Infrastructure.Extensions.Runtime;
using SelfClaw.Infrastructure.Extensions.Runtime.Models;
using SelfClaw.Infrastructure.Extensions.Models;
using SelfClaw.Infrastructure.Extensions.Skills;

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
            item.ResultSummary == "Read README.md." && item.ResultContent == "1\tbody");
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
            "list_files", "glob_files", "search_text", "read_file", "write_file", "edit_file", "run_shell_command");
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

    [Fact]
    public async Task StreamTurnAsync_applies_resolved_tools_instructions_adjustments_and_descriptors()
    {
        var tool = AIFunctionFactory.Create((Func<string>)(() => "done"), "custom_tool", "Custom tool");
        var client = new ScriptedChatClient([
            Update("m", new FunctionCallContent("call", "custom_tool", new Dictionary<string, object?>()))
        ]);
        var factory = new FakeChatClientFactory(client);
        var request = (DirectChatTurnRequest)CreateRequest(factory.Profile.Id);
        var userMessage = request.Messages.Single(message => message.Role == MessageRole.User);
        var capabilityLease = new DirectTurnCapabilityLease(
            ["Capability policy."],
            [tool],
            new Dictionary<string, DirectToolDescriptor>(StringComparer.Ordinal)
            {
                [tool.Name] = new(tool.Name, ToolCallKind.Search)
            },
            new Dictionary<Guid, string> { [userMessage.Id] = "adjusted prompt" },
            []);
        var runtime = CreateRuntime(factory, new FakeCapabilityResolver(capabilityLease));

        var events = await CollectAsync(runtime.StreamTurnAsync(request));

        factory.LastInputs!.Tools.Should().ContainSingle().Which.Name.Should().Be("custom_tool");
        client.LastMessages[0].Text.Should().Be("Follow project instructions.\n\nCapability policy.");
        client.LastMessages[1].Text.Should().Be("adjusted prompt");
        events.OfType<ToolCallStartedEvent>().Should().ContainSingle().Which.Kind.Should().Be(ToolCallKind.Search);
    }

    [Fact]
    public async Task StreamTurnAsync_does_not_create_provider_when_capability_resolution_fails()
    {
        var factory = new FakeChatClientFactory(new ScriptedChatClient([]));
        var runtime = CreateRuntime(factory, new ThrowingCapabilityResolver(
            new InvalidOperationException("capabilities unavailable")));

        var events = await CollectAsync(runtime.StreamTurnAsync(CreateRequest(factory.Profile.Id)));

        events.Should().ContainSingle().Which.Should().Be(
            new RunCompletedEvent(RunCompletionStatus.Failed, null, "capabilities unavailable"));
        factory.CreateCalls.Should().Be(0);
    }

    [Fact]
    public async Task StreamTurnAsync_disposes_provider_before_capability_lease()
    {
        var order = new List<string>();
        var client = new ScriptedChatClient([], dispose: () => order.Add("provider"));
        var capabilityLease = new DirectTurnCapabilityLease(
            [],
            [],
            new Dictionary<string, DirectToolDescriptor>(),
            new Dictionary<Guid, string>(),
            [],
            () =>
            {
                order.Add("capability");
                return ValueTask.CompletedTask;
            });
        var factory = new FakeChatClientFactory(client);

        await CollectAsync(CreateRuntime(factory, new FakeCapabilityResolver(capabilityLease))
            .StreamTurnAsync(CreateRequest(factory.Profile.Id)));

        order.Should().Equal("provider", "capability");
    }

    private static DirectAgentChatRuntime CreateRuntime(
        FakeChatClientFactory factory,
        IDirectTurnCapabilityResolver? capabilityResolver = null)
        => new(
            factory,
            capabilityResolver ?? new DirectTurnCapabilityResolver(
                new WorkspaceAgentToolset(new NoOpWorkspaceTools()),
                new EmptyExtensionPackageRepository(),
                new SkillPackageReader(new ExtensionPackageLimits(
                    1024 * 1024,
                    1024 * 1024,
                    100,
                    512 * 1024,
                    256 * 1024)),
                new SkillTokenParser(),
                new SkillRuntimeToolset()),
            new DirectPromptComposer());

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
        public int CreateCalls { get; private set; }

        public Task<AiChatClientLease> CreateAsync(
            Guid modelProfileId,
            AiChatRuntimeInputs inputs,
            CancellationToken cancellationToken = default)
        {
            CreateCalls++;
            LastInputs = inputs;
            return CreateLease();
        }

        public Task<AiChatClientLease> CreateForScopeAsync(
            string scope,
            AiChatRuntimeInputs inputs,
            CancellationToken cancellationToken = default)
        {
            CreateCalls++;
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
        private readonly Action? _dispose;

        public ScriptedChatClient(
            IReadOnlyList<ChatResponseUpdate> updates,
            Exception? terminalException = null,
            Action? dispose = null)
        {
            _updates = updates;
            _terminalException = terminalException;
            _dispose = dispose;
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
        public void Dispose()
        {
            IsDisposed = true;
            _dispose?.Invoke();
        }
    }

    private sealed class FakeCapabilityResolver : IDirectTurnCapabilityResolver
    {
        private readonly DirectTurnCapabilityLease _lease;

        public FakeCapabilityResolver(DirectTurnCapabilityLease lease)
        {
            _lease = lease;
        }

        public Task<DirectTurnCapabilityLease> ResolveAsync(
            DirectChatTurnRequest request,
            CancellationToken cancellationToken = default)
            => Task.FromResult(_lease);
    }

    private sealed class ThrowingCapabilityResolver : IDirectTurnCapabilityResolver
    {
        private readonly Exception _exception;

        public ThrowingCapabilityResolver(Exception exception)
        {
            _exception = exception;
        }

        public Task<DirectTurnCapabilityLease> ResolveAsync(
            DirectChatTurnRequest request,
            CancellationToken cancellationToken = default)
            => Task.FromException<DirectTurnCapabilityLease>(_exception);
    }

    private sealed class NoOpWorkspaceTools : IWorkspaceToolService
    {
        public Task<IReadOnlyList<WorkspaceFileEntry>> ListFilesAsync(string root, string? path, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<WorkspaceFileEntry>>([]);
        public Task<IReadOnlyList<WorkspaceFileEntry>> GlobFilesAsync(string root, string pattern, string? path = null, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<WorkspaceFileEntry>>([]);
        public Task<IReadOnlyList<WorkspaceSearchHit>> SearchTextAsync(string root, string query, WorkspaceSearchOptions? options = null, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<WorkspaceSearchHit>>([]);
        public Task<WorkspaceFileContent> ReadFileAsync(string root, string path, int? startLine = null, int? lineCount = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<WorkspaceFileWriteResult> WriteFileAsync(string root, string path, string content, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<WorkspaceFileWriteResult> EditFileAsync(string root, string path, string oldText, string newText, bool replaceAll = false, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ShellCommandResult> RunShellCommandAsync(string root, string command, int timeout, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class EmptyExtensionPackageRepository : IExtensionPackageRepository
    {
        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<ExtensionPackageRecord>> ListPackagesAsync(
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ExtensionPackageRecord>>([]);

        public Task<ExtensionPackageRecord?> GetPackageAsync(
            ExtensionKind kind,
            string id,
            CancellationToken cancellationToken = default)
            => Task.FromResult<ExtensionPackageRecord?>(null);

        public Task<ExtensionPackageRecord> UpsertPackageAsync(
            ExtensionPackageRecord package,
            CancellationToken cancellationToken = default)
            => Task.FromResult(package);

        public Task SetPackageEnabledAsync(
            ExtensionKind kind,
            string id,
            bool enabled,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task DeletePackageAsync(
            ExtensionKind kind,
            string id,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
