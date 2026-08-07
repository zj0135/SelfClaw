using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Core.Runtime.Agent;
using SelfClaw.Desktop.Services;
using SelfClaw.Desktop.Services.Runtime;

namespace SelfClaw.Tests.Desktop.Services.Runtime;

public sealed class ConversationTurnRecorderTests
{
    [Fact]
    public async Task ApplyEventAsync_records_the_shared_event_protocol_and_commits_the_terminal_state()
    {
        var context = CreateContext();

        context.Recorder.BeginTurn(context.Session, context.Turn);
        await context.ApplyAsync(new RunStartedEvent("session", "model", null));
        await context.ApplyAsync(new AssistantThinkingDeltaEvent("thinking", "reason"));
        await context.ApplyAsync(new AssistantTextDeltaEvent("text", "answer"));
        await context.ApplyAsync(new ToolCallStartedEvent(
            "call-1",
            "mcp__git__status",
            "{}",
            ToolCallKind.Read,
            ToolSourceKind.Mcp,
            "git",
            "status"));
        await context.ApplyAsync(new ToolCallCompletedEvent(
            "call-1",
            ToolCallStatus.Completed,
            "read 1 file",
            "contents"));
        await context.ApplyAsync(new UsageReportedEvent(11, 7));
        await context.ApplyAsync(new RunStatusEvent(AgentRunStatus.Thinking));
        await context.ApplyAsync(new RunCompletedEvent(RunCompletionStatus.Succeeded, "answer"));

        var finalization = context.Committer.Finalizations.Should().ContainSingle().Which;
        finalization.AssistantMessage.Id.Should().Be(context.TurnId);
        finalization.AssistantMessage.Status.Should().Be(MessageStatus.Completed);
        finalization.AssistantMessage.MarkdownContent.Should().Contain("reason").And.Contain("answer");
        finalization.AssistantMessage.InputTokens.Should().Be(11);
        finalization.AssistantMessage.OutputTokens.Should().Be(7);
        finalization.ToolExecutions.Should().ContainSingle();
        finalization.ToolExecutions[0].Status.Should().Be(ToolExecutionStatus.Completed);
        finalization.ToolExecutions[0].MessageId.Should().Be(context.TurnId);
        finalization.ToolExecutions[0].SourceKind.Should().Be(ToolSourceKind.Mcp);
        finalization.ToolExecutions[0].ResultContent.Should().Be("contents");
        context.Repository.ToolUpserts.Should().HaveCount(2);
        context.Session.ActivityText.Should().Be("正在思考...");
    }

    [Fact]
    public async Task FinalizeInterruptedAsync_preserves_partial_text_and_closes_running_tools()
    {
        var context = CreateContext();

        context.Recorder.BeginTurn(context.Session, context.Turn);
        await context.ApplyAsync(new AssistantTextDeltaEvent("text", "partial"));
        await context.ApplyAsync(new ToolCallStartedEvent(
            "call-1",
            "run_shell_command",
            "{}",
            ToolCallKind.Run));
        await context.Recorder.FinalizeInterruptedAsync(
            context.Session,
            context.Turn,
            TurnFinalizationKind.Cancelled,
            "Generation stopped.",
            context.Committer);

        var finalization = context.Committer.Finalizations.Should().ContainSingle().Which;
        finalization.AssistantMessage.Status.Should().Be(MessageStatus.Cancelled);
        finalization.AssistantMessage.MarkdownContent.Should().Contain("partial").And.Contain("selfclaw:tool:");
        finalization.AssistantMessage.ErrorMessage.Should().Be("Generation stopped.");
        finalization.ToolExecutions.Should().ContainSingle()
            .Which.Status.Should().Be(ToolExecutionStatus.Cancelled);
    }

    [Fact]
    public async Task ApplyEventAsync_ignores_a_duplicate_terminal_event()
    {
        var context = CreateContext();

        context.Recorder.BeginTurn(context.Session, context.Turn);
        await context.ApplyAsync(new RunCompletedEvent(RunCompletionStatus.Succeeded, "done"));
        await context.ApplyAsync(new RunCompletedEvent(
            RunCompletionStatus.Failed,
            null,
            "late failure"));

        context.Committer.Finalizations.Should().ContainSingle();
        context.Session.Messages.Single().Status.Should().Be(MessageStatus.Completed);
    }

    private static RecorderTestContext CreateContext()
    {
        var now = DateTimeOffset.UtcNow;
        var repository = new RecordingConversationRepository();
        var conversation = new ConversationRecord(
            Guid.NewGuid(),
            "Conversation",
            null,
            ToolPermissionMode.RequireApproval,
            "build",
            now,
            now);
        var session = new ConversationRuntimeState(
            conversation,
            [],
            [],
            new Dictionary<Guid, ToolRunAnchor>());
        var turnId = Guid.NewGuid();
        var turn = new AgentTurnState(turnId, new AgentRuntimeDefinition(
            "build",
            "Builder",
            "test",
            AgentExecutionMode.Direct,
            AgentRuntimeDefinition.SystemToolPolicy,
            [],
            [],
            [],
            [],
            string.Empty));
        var recorder = new ConversationTurnRecorder(
            repository,
            TimeProvider.System,
            NullLogger<ConversationTurnRecorder>.Instance);
        return new RecorderTestContext(
            turnId,
            repository,
            session,
            turn,
            recorder,
            new RecordingCommitter());
    }

    private sealed record RecorderTestContext(
        Guid TurnId,
        RecordingConversationRepository Repository,
        ConversationRuntimeState Session,
        AgentTurnState Turn,
        ConversationTurnRecorder Recorder,
        RecordingCommitter Committer)
    {
        public Task ApplyAsync(AgentStreamEvent streamEvent)
            => Recorder.ApplyEventAsync(
                Session,
                Turn,
                streamEvent,
                Committer,
                CancellationToken.None);
    }

    private sealed class RecordingCommitter : IRecordedTurnCommitter
    {
        public List<TurnFinalization> Finalizations { get; } = [];

        public Task<bool> TryCommitAsync(TurnFinalization finalization)
        {
            Finalizations.Add(finalization);
            return Task.FromResult(true);
        }
    }

    private sealed class RecordingConversationRepository : IConversationRepository
    {
        public List<ToolExecutionRecord> ToolUpserts { get; } = [];

        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<ConversationRecord>> ListConversationsAsync(
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ConversationRecord>>([]);

        public Task<ConversationRecord?> GetConversationAsync(
            Guid conversationId,
            CancellationToken cancellationToken = default)
            => Task.FromResult<ConversationRecord?>(null);

        public Task<ConversationRecord> UpsertConversationAsync(
            ConversationRecord conversation,
            CancellationToken cancellationToken = default)
            => Task.FromResult(conversation);

        public Task DeleteConversationAsync(
            Guid conversationId,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<MessageRecord>> ListMessagesAsync(
            Guid conversationId,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<MessageRecord>>([]);

        public Task<MessageRecord> UpsertMessageAsync(
            MessageRecord message,
            CancellationToken cancellationToken = default)
            => Task.FromResult(message);

        public Task<IReadOnlyList<ToolExecutionRecord>> ListToolExecutionsAsync(
            Guid conversationId,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ToolExecutionRecord>>([]);

        public Task<ToolExecutionRecord> UpsertToolExecutionAsync(
            ToolExecutionRecord record,
            CancellationToken cancellationToken = default)
        {
            ToolUpserts.Add(record);
            return Task.FromResult(record);
        }

        public Task<IReadOnlyList<WorkspaceRoot>> ListWorkspaceRootsAsync(
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<WorkspaceRoot>>([]);

        public Task<WorkspaceRoot> UpsertWorkspaceRootAsync(
            WorkspaceRoot workspaceRoot,
            CancellationToken cancellationToken = default)
            => Task.FromResult(workspaceRoot);

        public Task DeleteWorkspaceRootAsync(
            Guid workspaceRootId,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }
}
