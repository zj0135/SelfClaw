using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Core.Runtime.Agent;
using SelfClaw.Desktop.Services;
using SelfClaw.Desktop.Services.Runtime;

namespace SelfClaw.Tests.Desktop.Services.Runtime;

public sealed class ConversationTurnEngineTests
{
    [Fact]
    public async Task ApplyEventAsync_reduces_a_successful_turn_into_message_tool_and_anchor()
    {
        var repository = new FakeConversationRepository();
        var engine = CreateEngine(repository);
        var session = CreateSession();
        var turn = new AgentTurnState(CreateAgent());
        var immediatePublishes = 0;
        var throttledPublishes = 0;
        session.TranscriptChanged += immediate =>
        {
            if (immediate)
            {
                immediatePublishes++;
            }
            else
            {
                throttledPublishes++;
            }
        };

        await engine.ApplyEventAsync(session, turn, new RunStartedEvent("s1", "model", null), default);
        await engine.ApplyEventAsync(session, turn, new AssistantTextDeltaEvent("b", "hello "), default);
        await engine.ApplyEventAsync(session, turn, new ToolCallStartedEvent("call-1", "read_file", "{}", ToolCallKind.Read), default);
        await engine.ApplyEventAsync(session, turn, new ToolCallCompletedEvent("call-1", ToolCallStatus.Completed, "read 1 file", "contents"), default);
        await engine.ApplyEventAsync(session, turn, new AssistantTextDeltaEvent("b", "world"), default);
        await engine.ApplyEventAsync(session, turn, new UsageReportedEvent(11, 7), default);
        await engine.ApplyEventAsync(session, turn, new RunCompletedEvent(RunCompletionStatus.Succeeded, "hello world"), default);

        var assistant = session.Messages.Single(item => item.Id == turn.AssistantMessageId);
        assistant.Status.Should().Be(MessageStatus.Completed);
        // The tool call arrived mid-stream, so its anchor is inlined between the two text deltas; the
        // final text merges around it rather than collapsing to a single contiguous "hello world".
        assistant.MarkdownContent.Should().Contain("hello ").And.Contain("world");
        assistant.MarkdownContent.Should().Contain("selfclaw:tool:");
        assistant.InputTokens.Should().Be(11);
        assistant.OutputTokens.Should().Be(7);

        session.ToolRuns.Should().ContainSingle()
            .Which.Status.Should().Be(ToolExecutionStatus.Completed);
        session.ToolRunAnchors.Should().ContainKey(session.ToolRuns[0].Id);
        session.ActiveMessageIds.Should().BeEmpty();

        // Running tool starts / completes are persisted as they arrive (2 upserts here); the terminal
        // assistant + tool state goes through the atomic finalizer, not the streaming repository path.
        repository.ToolUpserts.Should().Be(2);
        immediatePublishes.Should().Be(1, "the terminal snapshot flushes immediately");
        throttledPublishes.Should().BeGreaterThan(0, "streaming deltas publish through the throttle");
    }

    [Fact]
    public async Task ApplyEventAsync_maps_failed_completion_to_failed_message()
    {
        var engine = CreateEngine(new FakeConversationRepository());
        var session = CreateSession();
        var turn = new AgentTurnState(CreateAgent());

        await engine.ApplyEventAsync(session, turn, new RunStartedEvent(null, null, null), default);
        await engine.ApplyEventAsync(
            session,
            turn,
            new RunCompletedEvent(RunCompletionStatus.Failed, null, "provider exploded"),
            default);

        var assistant = session.Messages.Single(item => item.Id == turn.AssistantMessageId);
        assistant.Status.Should().Be(MessageStatus.Failed);
        assistant.ErrorMessage.Should().Be("provider exploded");
    }

    [Fact]
    public async Task FinalizeInterruptedAsync_cancels_message_and_closes_running_tool()
    {
        var engine = CreateEngine(new FakeConversationRepository());
        var session = CreateSession();
        var turn = new AgentTurnState(CreateAgent());

        engine.BeginAssistantMessage(session, turn);
        await engine.ApplyEventAsync(session, turn, new AssistantTextDeltaEvent("b", "partial"), default);
        await engine.ApplyEventAsync(session, turn, new ToolCallStartedEvent("call-1", "run_shell_command", "{}", ToolCallKind.Run), default);

        await engine.FinalizeInterruptedAsync(session, turn, TurnFinalizationKind.Cancelled, "Generation stopped.");

        var assistant = session.Messages.Single(item => item.Id == turn.AssistantMessageId);
        assistant.Status.Should().Be(MessageStatus.Cancelled);
        assistant.MarkdownContent.Should().Contain("partial");
        assistant.ErrorMessage.Should().Be("Generation stopped.");
        session.ToolRuns.Should().ContainSingle()
            .Which.Status.Should().Be(ToolExecutionStatus.Cancelled);
        session.ActiveMessageIds.Should().BeEmpty();
    }

    [Fact]
    public async Task FinalizeInterruptedAsync_is_idempotent_after_a_terminal_event()
    {
        var engine = CreateEngine(new FakeConversationRepository());
        var session = CreateSession();
        var turn = new AgentTurnState(CreateAgent());

        await engine.ApplyEventAsync(session, turn, new RunStartedEvent(null, null, null), default);
        await engine.ApplyEventAsync(session, turn, new RunCompletedEvent(RunCompletionStatus.Succeeded, "done"), default);

        await engine.FinalizeInterruptedAsync(session, turn, TurnFinalizationKind.Failed, "late failure");

        session.Messages.Single(item => item.Id == turn.AssistantMessageId)
            .Status.Should().Be(MessageStatus.Completed);
    }

    private static ConversationTurnEngine CreateEngine(IConversationRepository repository)
    {
        var finalizer = new DesktopTurnFinalizer(
            new NoOpFinalizationRepository(),
            NullLogger<DesktopTurnFinalizer>.Instance);
        return new ConversationTurnEngine(repository, finalizer, NullLogger<ConversationTurnEngine>.Instance);
    }

    private static ConversationRuntimeState CreateSession()
    {
        var now = DateTimeOffset.UtcNow;
        var conversation = new ConversationRecord(
            Guid.NewGuid(),
            "New chat",
            null,
            ToolPermissionMode.RequireApproval,
            "build",
            now,
            now);
        return new ConversationRuntimeState(conversation, [], [], new Dictionary<Guid, ToolRunAnchor>());
    }

    private static AgentRuntimeDefinition CreateAgent()
        => new(
            "build",
            "Builder",
            "test",
            AgentExecutionMode.Cli,
            AgentRuntimeDefinition.SystemToolPolicy,
            [],
            [],
            [],
            "");

    private sealed class FakeConversationRepository : IConversationRepository
    {
        public int ToolUpserts { get; private set; }

        public Task<ToolExecutionRecord> UpsertToolExecutionAsync(
            ToolExecutionRecord record,
            CancellationToken cancellationToken = default)
        {
            ToolUpserts++;
            return Task.FromResult(record);
        }

        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<ConversationRecord>> ListConversationsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ConversationRecord>>([]);

        public Task<ConversationRecord?> GetConversationAsync(Guid conversationId, CancellationToken cancellationToken = default)
            => Task.FromResult<ConversationRecord?>(null);

        public Task<ConversationRecord> UpsertConversationAsync(ConversationRecord conversation, CancellationToken cancellationToken = default)
            => Task.FromResult(conversation);

        public Task DeleteConversationAsync(Guid conversationId, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<MessageRecord>> ListMessagesAsync(Guid conversationId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<MessageRecord>>([]);

        public Task<MessageRecord> UpsertMessageAsync(MessageRecord message, CancellationToken cancellationToken = default)
            => Task.FromResult(message);

        public Task<IReadOnlyList<ToolExecutionRecord>> ListToolExecutionsAsync(Guid conversationId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ToolExecutionRecord>>([]);

        public Task<IReadOnlyList<WorkspaceRoot>> ListWorkspaceRootsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<WorkspaceRoot>>([]);

        public Task<WorkspaceRoot> UpsertWorkspaceRootAsync(WorkspaceRoot workspaceRoot, CancellationToken cancellationToken = default)
            => Task.FromResult(workspaceRoot);

        public Task DeleteWorkspaceRootAsync(Guid workspaceRootId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class NoOpFinalizationRepository : ITurnFinalizationRepository
    {
        public Task<bool> TryFinalizeTurnAsync(TurnFinalization finalization, CancellationToken cancellationToken = default)
            => Task.FromResult(true);
    }
}
