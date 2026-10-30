using SelfClaw.Desktop.Services.Questions;
using SelfClaw.Desktop.Services.Tools;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using SelfClaw.Core.Runtime;
using SelfClaw.Core.Runtime.Agent;
using SelfClaw.Desktop.Services.AgentActivity;

namespace SelfClaw.Tests.Desktop.Services.AgentActivity;

public sealed class AgentActivityCoordinatorTests
{
    [Fact]
    public void ApplyEvent_projects_major_nodes_and_deduplicates_stream_deltas()
    {
        var handler = new DesktopToolApprovalHandler();
        using var coordinator = CreateCoordinator(handler);
        var context = CreateContext("build");
        var snapshots = new List<AgentActivitySnapshot>();
        coordinator.SnapshotChanged += (_, snapshot) => snapshots.Add(snapshot);

        coordinator.BeginTurn(context);
        coordinator.ApplyEvent(context.TurnId, new RunStartedEvent("session", "gpt-test", null));
        coordinator.ApplyEvent(context.TurnId, new AssistantThinkingDeltaEvent("thinking", "first"));
        var countAfterFirstThinking = snapshots.Count;
        coordinator.ApplyEvent(context.TurnId, new AssistantThinkingDeltaEvent("thinking", "second"));
        coordinator.ApplyEvent(
            context.TurnId,
            new ToolCallCompletedEvent("missing-call", ToolCallStatus.Completed, "ignored", null));
        coordinator.ApplyEvent(
            context.TurnId,
            new ToolCallStartedEvent("call-1", "read_file", "{}", ToolCallKind.Read));
        coordinator.ApplyEvent(
            context.TurnId,
            new ToolCallCompletedEvent("call-1", ToolCallStatus.Completed, "done", null));
        coordinator.ApplyEvent(
            context.TurnId,
            new RunCompletedEvent(RunCompletionStatus.Succeeded, "done"));

        countAfterFirstThinking.Should().Be(3);
        snapshots.Should().HaveCount(6, "the second thinking delta carries no new major node");
        snapshots[2].Phase.Should().Be(AgentActivityPhase.Thinking);
        snapshots[3].Should().Match<AgentActivitySnapshot>(snapshot =>
            snapshot.Phase == AgentActivityPhase.UsingTool && snapshot.ToolKind == ToolCallKind.Read);
        coordinator.CurrentSnapshot.Phase.Should().Be(AgentActivityPhase.Succeeded);
        coordinator.CurrentSnapshot.ActiveTurnCount.Should().Be(0);
    }

    [Fact]
    public async Task Approval_projection_uses_one_fifo_and_restores_the_turn_after_resolution()
    {
        var handler = new DesktopToolApprovalHandler();
        using var coordinator = CreateCoordinator(handler);
        var context = CreateContext("build");
        coordinator.BeginTurn(context);
        coordinator.ApplyEvent(context.TurnId, new RunStatusEvent(AgentRunStatus.Thinking));

        var first = CreateApproval(context.ConversationId, "run_shell_command", "Run command");
        var second = CreateApproval(context.ConversationId, "write_file", "Write file");
        var firstDecision = handler.RequestApprovalAsync(first);
        var secondDecision = handler.RequestApprovalAsync(second);

        coordinator.CurrentSnapshot.Phase.Should().Be(AgentActivityPhase.AwaitingApproval);
        coordinator.CurrentSnapshot.Approval.Should().Be(first);
        coordinator.CurrentSnapshot.Headline.Should().Be("等待「Run command」审批…");
        coordinator.CurrentSnapshot.Detail.Should().BeNull();

        handler.TryResolve(first.ToolExecutionId, ToolApprovalDecision.Approved).Should().BeTrue();
        (await firstDecision).Should().Be(ToolApprovalDecision.Approved);
        coordinator.CurrentSnapshot.Approval.Should().Be(second);
        coordinator.CurrentSnapshot.Headline.Should().Be("等待「Write file」审批…");

        handler.TryResolve(second.ToolExecutionId, ToolApprovalDecision.Denied).Should().BeTrue();
        (await secondDecision).Should().Be(ToolApprovalDecision.Denied);
        coordinator.CurrentSnapshot.Approval.Should().BeNull();
        coordinator.CurrentSnapshot.Phase.Should().Be(AgentActivityPhase.Thinking);
    }

    [Fact]
    public void Selected_conversation_wins_over_a_more_recent_background_turn()
    {
        var handler = new DesktopToolApprovalHandler();
        using var coordinator = CreateCoordinator(handler);
        var selected = CreateContext("selected");
        var background = CreateContext("background");
        coordinator.BeginTurn(selected);
        coordinator.BeginTurn(background);
        coordinator.ApplyEvent(background.TurnId, new RunStatusEvent(AgentRunStatus.Running));

        coordinator.SetSelectedConversation(selected.ConversationId);

        coordinator.CurrentSnapshot.ConversationId.Should().Be(selected.ConversationId);
        coordinator.CurrentSnapshot.AgentName.Should().Be("selected");
        coordinator.CurrentSnapshot.ActiveTurnCount.Should().Be(2);
    }

    [Fact]
    public void CompleteInterrupted_projects_cancellation_without_a_terminal_event()
    {
        var handler = new DesktopToolApprovalHandler();
        using var coordinator = CreateCoordinator(handler);
        var context = CreateContext("build");
        coordinator.BeginTurn(context);

        coordinator.CompleteInterrupted(
            context.TurnId,
            AgentActivityOutcome.Cancelled,
            "Generation stopped.");

        coordinator.CurrentSnapshot.Phase.Should().Be(AgentActivityPhase.Cancelled);
        coordinator.CurrentSnapshot.Headline.Should().Be("任务已停止");
        coordinator.CurrentSnapshot.ActiveTurnCount.Should().Be(0);
    }

    [Fact]
    public async Task Throwing_snapshot_subscriber_does_not_reject_an_approval()
    {
        var handler = new DesktopToolApprovalHandler();
        using var coordinator = CreateCoordinator(handler);
        coordinator.SnapshotChanged += (_, _) => throw new InvalidOperationException("UI failed");
        var request = CreateApproval(Guid.NewGuid(), "write_file", "Write file");

        var decision = handler.RequestApprovalAsync(request);

        decision.IsCompleted.Should().BeFalse();
        handler.TryResolve(request.ToolExecutionId, ToolApprovalDecision.Approved).Should().BeTrue();
        (await decision).Should().Be(ToolApprovalDecision.Approved);
    }

    [Fact]
    public async Task UserQuestion_pending_outranks_a_pending_approval_in_the_snapshot()
    {
        var approvals = new DesktopToolApprovalHandler();
        var questions = new DesktopUserQuestionHandler();
        using var coordinator = CreateCoordinator(approvals, questions);
        var context = CreateContext("build");
        coordinator.BeginTurn(context);
        coordinator.ApplyEvent(context.TurnId, new RunStatusEvent(AgentRunStatus.Thinking));

        var approval = CreateApproval(context.ConversationId, "write_file", "Write file");
        var approvalDecision = approvals.RequestApprovalAsync(approval);
        var question = CreateQuestion(context.ConversationId);
        var pendingQuestion = questions.RequestAnswerAsync(question);

        // The question has already stalled its turn, so it wins the single activity slot.
        coordinator.CurrentSnapshot.Phase.Should().Be(AgentActivityPhase.AwaitingAnswer);
        coordinator.CurrentSnapshot.Headline.Should().Be("等待你的回答…");
        coordinator.CurrentSnapshot.Question.Should().Be(question);
        coordinator.CurrentSnapshot.Approval.Should().BeNull();
        coordinator.CurrentSnapshot.ConversationId.Should().Be(context.ConversationId);

        questions.TryAnswer(question.RequestId, new UserQuestionAnswer(question.RequestId, [], Skipped: true))
            .Should().BeTrue();
        await pendingQuestion;

        coordinator.CurrentSnapshot.Phase.Should().Be(AgentActivityPhase.AwaitingApproval);
        coordinator.CurrentSnapshot.Question.Should().BeNull();
        coordinator.CurrentSnapshot.Approval.Should().Be(approval);

        approvals.TryResolve(approval.ToolExecutionId, ToolApprovalDecision.Approved).Should().BeTrue();
        await approvalDecision;
        coordinator.CurrentSnapshot.Phase.Should().Be(AgentActivityPhase.Thinking);
    }

    private static AgentActivityCoordinator CreateCoordinator(
        DesktopToolApprovalHandler handler, DesktopUserQuestionHandler? questions = null)
        => new(handler, questions ?? new DesktopUserQuestionHandler(), NullLogger<AgentActivityCoordinator>.Instance);

    private static AgentActivityContext CreateContext(string agentName)
        => new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            $"{agentName} conversation",
            agentName,
            agentName,
            AgentExecutionMode.Direct,
            DateTimeOffset.UtcNow);

    private static ToolApprovalRequest CreateApproval(
        Guid conversationId,
        string toolName,
        string displayName)
        => new(
            Guid.NewGuid(),
            toolName,
            displayName,
            displayName,
            "{}",
            conversationId);

    private static UserQuestionRequest CreateQuestion(Guid conversationId)
        => new(
            Guid.NewGuid(),
            [
                new UserQuestionPrompt(
                    Guid.NewGuid(),
                    "Which database should this service use?",
                    "Database",
                    [new UserQuestionOption("PostgreSQL", "Relational"), new UserQuestionOption("SQLite", "Embedded")],
                    MultiSelect: false)
            ],
            conversationId);
}
