using System.Runtime.CompilerServices;
using FluentAssertions;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Core.Runtime.Agent;
using SelfClaw.Infrastructure.Agents.Cli;
using SelfClaw.Infrastructure.Agents.Cli.Adapters;
using SelfClaw.Infrastructure.Agents.Cli.Adapters.Abstractions;
using SelfClaw.Infrastructure.Agents.Cli.Process;
using SelfClaw.Infrastructure.Agents.Cli.Process.Abstractions;
using SelfClaw.Infrastructure.Agents.Cli.Process.Models;
using SelfClaw.Infrastructure.Agents.Cli.Session.Abstractions;

namespace SelfClaw.Tests.Infrastructure.Agents.Cli;

public sealed class CliAgentChatRuntimeTests
{
    [Fact]
    public async Task Claude_turn_combines_fresh_session_arguments_json_input_and_parser()
    {
        var conversationId = Guid.NewGuid();
        var session = new FakeProcessSession(
            [
                """{"type":"system","subtype":"init","session_id":"claude-session","model":"claude-sonnet"}""",
                """{"type":"assistant","message":{"id":"message-1","content":[{"type":"text","text":"Claude answer"}]}}""",
                """{"type":"result","subtype":"success","is_error":false,"result":"Claude answer"}""",
            ]);
        var (runtime, host, store) = CreateRuntime(session);
        var request = CreateRequest(
            conversationId,
            CliAgentKind.Claude,
            model: "sonnet",
            reasoningEffort: "high",
            instructions: "Follow workspace instructions.");

        var events = await CollectAsync(runtime.StreamTurnAsync(request));

        var startInfo = host.StartInfo ?? throw new InvalidOperationException("The process was not started.");
        Path.GetFileName(startInfo.Invocation.FileName).Should().Be("claude");
        startInfo.Invocation.ArgumentList.Should().Contain("--input-format");
        startInfo.Invocation.ArgumentList.Should().Contain("--include-partial-messages");
        startInfo.Invocation.ArgumentList.Should().ContainInOrder("--model", "sonnet");
        startInfo.Invocation.ArgumentList.Should().ContainInOrder("--effort", "high");
        startInfo.Invocation.ArgumentList.Should().ContainInOrder(
            "--append-system-prompt",
            "Follow workspace instructions.");
        var sessionIdIndex = startInfo.Invocation.ArgumentList.ToList().IndexOf("--session-id");
        sessionIdIndex.Should().BeGreaterThanOrEqualTo(0);
        Guid.TryParse(startInfo.Invocation.ArgumentList[sessionIdIndex + 1], out _).Should().BeTrue();
        session.StandardInputLines.Should().ContainSingle()
            .Which.Should().Contain("\"text\":\"test prompt\"");
        session.StandardInputCompleted.Should().BeTrue();
        store.Sessions[(conversationId, CliAgentKind.Claude)].Should().Be("claude-session");
        events.OfType<AssistantTextDeltaEvent>().Should().ContainSingle()
            .Which.Delta.Should().Be("Claude answer");
        events.OfType<RunCompletedEvent>().Should().ContainSingle()
            .Which.Status.Should().Be(RunCompletionStatus.Succeeded);
    }

    [Fact]
    public async Task Codex_turn_combines_resume_arguments_text_input_and_parser()
    {
        var conversationId = Guid.NewGuid();
        var session = new FakeProcessSession(
            [
                """{"type":"thread.started","thread_id":"new-thread"}""",
                """{"type":"item.completed","item":{"id":"message-1","type":"agent_message","text":"Codex answer"}}""",
                """{"type":"turn.completed","usage":{"input_tokens":5,"output_tokens":3}}""",
            ]);
        var (runtime, host, store) = CreateRuntime(session);
        store.Sessions[(conversationId, CliAgentKind.Codex)] = "stored-thread";
        var request = CreateRequest(
            conversationId,
            CliAgentKind.Codex,
            model: "gpt-5",
            reasoningEffort: "high");

        var events = await CollectAsync(runtime.StreamTurnAsync(request));

        var startInfo = host.StartInfo ?? throw new InvalidOperationException("The process was not started.");
        Path.GetFileName(startInfo.Invocation.FileName).Should().Be("codex");
        startInfo.Invocation.ArgumentList.Should().ContainInOrder("exec", "resume", "stored-thread");
        startInfo.Invocation.ArgumentList.Should().Contain("--json");
        startInfo.Invocation.ArgumentList.Should().ContainInOrder("--model", "gpt-5");
        startInfo.Invocation.ArgumentList.Should().ContainInOrder(
            "-c",
            "model_reasoning_effort=\"high\"");
        session.StandardInputLines.Should().Equal("test prompt");
        store.Sessions[(conversationId, CliAgentKind.Codex)].Should().Be("new-thread");
        events.OfType<AssistantTextDeltaEvent>().Should().ContainSingle()
            .Which.Delta.Should().Be("Codex answer");
        events.OfType<UsageReportedEvent>().Should().ContainSingle()
            .Which.Should().Be(new UsageReportedEvent(5, 3));
        events.OfType<RunCompletedEvent>().Should().ContainSingle()
            .Which.Status.Should().Be(RunCompletionStatus.Succeeded);
    }

    [Fact]
    public async Task OpenCode_turn_combines_resume_arguments_text_input_and_parser()
    {
        var conversationId = Guid.NewGuid();
        var session = new FakeProcessSession(
            [
                """{"type":"step_start","sessionID":"new-session"}""",
                """{"type":"text","part":{"id":"part-1","text":"OpenCode answer"}}""",
                """{"type":"step_finish","part":{"tokens":{"input":7,"output":4}}}""",
            ]);
        var (runtime, host, store) = CreateRuntime(session);
        store.Sessions[(conversationId, CliAgentKind.OpenCode)] = "stored-session";
        var request = CreateRequest(
            conversationId,
            CliAgentKind.OpenCode,
            model: "opencode/model",
            reasoningEffort: "ignored");

        var events = await CollectAsync(runtime.StreamTurnAsync(request));

        var startInfo = host.StartInfo ?? throw new InvalidOperationException("The process was not started.");
        Path.GetFileName(startInfo.Invocation.FileName).Should().Be("opencode");
        startInfo.Invocation.ArgumentList.Should().ContainInOrder("run", "--format", "json");
        startInfo.Invocation.ArgumentList.Should().ContainInOrder("-s", "stored-session");
        startInfo.Invocation.ArgumentList.Should().ContainInOrder("--model", "opencode/model");
        startInfo.Invocation.ArgumentList.Should().NotContain("ignored");
        session.StandardInputLines.Should().Equal("test prompt");
        store.Sessions[(conversationId, CliAgentKind.OpenCode)].Should().Be("new-session");
        events.OfType<AssistantTextDeltaEvent>().Should().ContainSingle()
            .Which.Delta.Should().Be("OpenCode answer");
        events.OfType<UsageReportedEvent>().Should().ContainSingle()
            .Which.Should().Be(new UsageReportedEvent(7, 4));
        events.OfType<RunCompletedEvent>().Should().ContainSingle()
            .Which.Status.Should().Be(RunCompletionStatus.Succeeded);
    }

    private static (CliAgentChatRuntime Runtime, FakeProcessHost Host, FakeSessionStore Store) CreateRuntime(
        FakeProcessSession session)
    {
        var host = new FakeProcessHost(session);
        var store = new FakeSessionStore();
        var registry = new CliAgentAdapterRegistry(
            new ICliAgentAdapter[]
            {
                new ClaudeCliAgentAdapter(),
                new CodexCliAgentAdapter(),
                new OpenCodeCliAgentAdapter(),
            });
        var commandResolver = new CliCommandResolver(
            path: Path.Combine(Path.GetTempPath(), "selfclaw-cli-tests"),
            isWindows: false,
            fileExists: _ => true);
        var runtime = new CliAgentChatRuntime(host, registry, commandResolver, store);
        return (runtime, host, store);
    }

    private static ChatTurnRequest CreateRequest(
        Guid conversationId,
        CliAgentKind kind,
        string? model,
        string? reasoningEffort,
        string instructions = "")
    {
        var now = DateTimeOffset.UtcNow;
        var message = new MessageRecord(
            Guid.NewGuid(),
            conversationId,
            MessageRole.User,
            "test prompt",
            MessageStatus.Completed,
            now,
            now);
        var agent = new AgentRuntimeDefinition(
            "cli-test",
            "CLI Test",
            "test",
            AgentExecutionMode.Cli,
            AgentRuntimeDefinition.SystemToolPolicy,
            [],
            [],
            [],
            [],
            instructions);

        return new CliChatTurnRequest(
            Guid.NewGuid(),
            conversationId,
            WorkspaceRoot: null,
            agent,
            [message],
            kind,
            model,
            reasoningEffort);
    }

    private static async Task<List<AgentStreamEvent>> CollectAsync(IAsyncEnumerable<AgentStreamEvent> source)
    {
        var events = new List<AgentStreamEvent>();
        await foreach (var streamEvent in source)
            events.Add(streamEvent);
        return events;
    }

    private sealed class FakeProcessHost : ICliAgentProcessHost
    {
        private readonly FakeProcessSession _session;

        public FakeProcessHost(FakeProcessSession session)
        {
            _session = session;
        }

        public CliProcessStartInfo? StartInfo { get; private set; }

        public ICliAgentProcessSession Start(
            CliProcessStartInfo startInfo,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StartInfo = startInfo;
            return _session;
        }
    }

    private sealed class FakeProcessSession : ICliAgentProcessSession
    {
        private readonly IReadOnlyList<string> _outputLines;

        public FakeProcessSession(IReadOnlyList<string> outputLines)
        {
            _outputLines = outputLines;
        }

        public List<string> StandardInputLines { get; } = [];

        public bool StandardInputCompleted { get; private set; }

        public Task WriteStdinLineAsync(string line, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StandardInputLines.Add(line);
            return Task.CompletedTask;
        }

        public Task CompleteStdinAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StandardInputCompleted = true;
            return Task.CompletedTask;
        }

        public async IAsyncEnumerable<string> ReadOutputLinesAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var line in _outputLines)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return line;
                await Task.Yield();
            }
        }

        public Task<CliProcessResult> WaitForExitAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new CliProcessResult(RunCompletionStatus.Succeeded, 0, StandardError: null));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeSessionStore : ICliAgentSessionStore
    {
        public Dictionary<(Guid ConversationId, CliAgentKind Kind), string> Sessions { get; } = [];

        public Task<string?> GetSessionIdAsync(
            Guid conversationId,
            CliAgentKind agentKind,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(
                Sessions.TryGetValue((conversationId, agentKind), out var sessionId) ? sessionId : null);
        }

        public Task SetSessionIdAsync(
            Guid conversationId,
            CliAgentKind agentKind,
            string sessionId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Sessions[(conversationId, agentKind)] = sessionId;
            return Task.CompletedTask;
        }
    }
}
