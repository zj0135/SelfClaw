using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using SelfClaw.Core.Runtime;
using SelfClaw.Core.Runtime.Agent;
using SelfClaw.Desktop.Services;
using SelfClaw.Desktop.Services.AgentActivity;

namespace SelfClaw.Tests.Desktop.Services.AgentActivity;

public sealed class AgentActivityCoordinatorTests
{
    [Fact]
    public void ApplyEvent_projects_major_nodes_and_deduplicates_stream_deltas()
    {
        var handler = new DesktopToolApprovalHandler(TimeSpan.FromSeconds(5));
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
        var handler = new DesktopToolApprovalHandler(TimeSpan.FromSeconds(5));
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
        coordinator.CurrentSnapshot.PendingApprovalCount.Should().Be(2);

        coordinator.TryResolveApproval(first.ToolExecutionId, approved: true).Should().BeTrue();
        (await firstDecision).Should().BeTrue();
        coordinator.CurrentSnapshot.Approval.Should().Be(second);
        coordinator.CurrentSnapshot.PendingApprovalCount.Should().Be(1);

        coordinator.TryResolveApproval(second.ToolExecutionId, approved: false).Should().BeTrue();
        (await secondDecision).Should().BeFalse();
        coordinator.CurrentSnapshot.Approval.Should().BeNull();
        coordinator.CurrentSnapshot.Phase.Should().Be(AgentActivityPhase.Thinking);
    }

    [Fact]
    public void Selected_conversation_wins_over_a_more_recent_background_turn()
    {
        var handler = new DesktopToolApprovalHandler(TimeSpan.FromSeconds(5));
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
        var handler = new DesktopToolApprovalHandler(TimeSpan.FromSeconds(5));
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
        var handler = new DesktopToolApprovalHandler(TimeSpan.FromSeconds(5));
        using var coordinator = CreateCoordinator(handler);
        coordinator.SnapshotChanged += (_, _) => throw new InvalidOperationException("UI failed");
        var request = CreateApproval(Guid.NewGuid(), "write_file", "Write file");

        var decision = handler.RequestApprovalAsync(request);

        decision.IsCompleted.Should().BeFalse();
        coordinator.TryResolveApproval(request.ToolExecutionId, approved: true).Should().BeTrue();
        (await decision).Should().BeTrue();
    }

    private static AgentActivityCoordinator CreateCoordinator(DesktopToolApprovalHandler handler)
        => new(handler, NullLogger<AgentActivityCoordinator>.Instance);

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
}
