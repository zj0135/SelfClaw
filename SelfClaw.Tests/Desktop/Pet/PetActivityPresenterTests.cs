using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using SelfClaw.Core.Runtime;
using SelfClaw.Core.Runtime.Agent;
using SelfClaw.Desktop.Pet;
using SelfClaw.Desktop.Services;
using SelfClaw.Desktop.Services.AgentActivity;

namespace SelfClaw.Tests.Desktop.Pet;

public sealed class PetActivityPresenterTests
{
    [Fact]
    public void Active_bubble_auto_hides_without_stopping_the_work_animation()
    {
        var handler = new DesktopToolApprovalHandler(TimeSpan.FromSeconds(5));
        using var coordinator = CreateCoordinator(handler);
        var scheduler = new ManualPetPresentationScheduler();
        using var presenter = CreatePresenter(coordinator, scheduler);
        var context = CreateContext();

        coordinator.BeginTurn(context);

        presenter.Current.Should().Match<PetBubbleViewState>(state =>
            state.IsVisible && state.WorkState == PetWorkState.Working);
        scheduler.Delay.Should().Be(TimeSpan.FromSeconds(4));

        scheduler.Fire();

        presenter.Current.Should().Match<PetBubbleViewState>(state =>
            !state.IsVisible && state.WorkState == PetWorkState.Working);
    }

    [Fact]
    public void Terminal_bubble_auto_hide_clears_terminal_animation_and_toggle_only_restores_content()
    {
        var handler = new DesktopToolApprovalHandler(TimeSpan.FromSeconds(5));
        using var coordinator = CreateCoordinator(handler);
        var scheduler = new ManualPetPresentationScheduler();
        using var presenter = CreatePresenter(coordinator, scheduler);
        var context = CreateContext();
        coordinator.BeginTurn(context);

        coordinator.ApplyEvent(
            context.TurnId,
            new RunCompletedEvent(RunCompletionStatus.Failed, "model failed"));

        presenter.Current.WorkState.Should().Be(PetWorkState.Failed);
        scheduler.Delay.Should().Be(TimeSpan.FromSeconds(8));

        scheduler.Fire();

        presenter.Current.IsVisible.Should().BeFalse();
        presenter.Current.WorkState.Should().Be(PetWorkState.None);

        presenter.ToggleBubble();

        presenter.Current.IsVisible.Should().BeTrue();
        presenter.Current.Headline.Should().Be("任务失败");
        presenter.Current.WorkState.Should().Be(PetWorkState.None);
        scheduler.Delay.Should().Be(TimeSpan.FromSeconds(8));
    }

    [Fact]
    public async Task Approval_state_is_pinned_uses_fifo_detail_and_resolves_the_current_request()
    {
        var handler = new DesktopToolApprovalHandler(TimeSpan.FromSeconds(5));
        using var coordinator = CreateCoordinator(handler);
        var scheduler = new ManualPetPresentationScheduler();
        using var presenter = CreatePresenter(coordinator, scheduler);
        var context = CreateContext();
        coordinator.BeginTurn(context);
        var first = CreateApproval(context.ConversationId, "run_shell_command", "Run command");
        var second = CreateApproval(context.ConversationId, "write_file", "Write file");

        var firstDecision = handler.RequestApprovalAsync(first);
        var secondDecision = handler.RequestApprovalAsync(second);

        presenter.Current.Should().Match<PetBubbleViewState>(state =>
            state.IsPinned &&
            state.ApprovalId == first.ToolExecutionId &&
            state.Detail != null &&
            state.Detail.Contains("还有 1 个请求", StringComparison.Ordinal));
        scheduler.IsScheduled.Should().BeFalse();

        presenter.DismissBubble();
        presenter.Current.IsVisible.Should().BeTrue();

        presenter.TryResolveCurrentApproval(approved: true).Should().BeTrue();
        (await firstDecision).Should().BeTrue();
        presenter.Current.ApprovalId.Should().Be(second.ToolExecutionId);

        presenter.TryResolveCurrentApproval(approved: false).Should().BeTrue();
        (await secondDecision).Should().BeFalse();
        presenter.Current.IsPinned.Should().BeFalse();
    }

    [Fact]
    public void Conversation_activation_uses_the_presented_conversation()
    {
        var handler = new DesktopToolApprovalHandler(TimeSpan.FromSeconds(5));
        using var coordinator = CreateCoordinator(handler);
        using var presenter = CreatePresenter(coordinator, new ManualPetPresentationScheduler());
        var context = CreateContext();
        Guid? activatedConversationId = null;
        presenter.ConversationActivationRequested += (_, id) => activatedConversationId = id;
        coordinator.BeginTurn(context);

        presenter.RequestCurrentConversationActivation().Should().BeTrue();

        activatedConversationId.Should().Be(context.ConversationId);
    }

    [Fact]
    public void Throwing_state_subscriber_does_not_break_presentation_lifecycle()
    {
        var handler = new DesktopToolApprovalHandler(TimeSpan.FromSeconds(5));
        using var coordinator = CreateCoordinator(handler);
        var scheduler = new ManualPetPresentationScheduler();
        using var presenter = CreatePresenter(coordinator, scheduler);
        presenter.StateChanged += (_, _) => throw new InvalidOperationException("view failed");

        coordinator.BeginTurn(CreateContext());
        scheduler.Fire();

        presenter.Current.IsVisible.Should().BeFalse();
    }

    private static PetActivityPresenter CreatePresenter(
        AgentActivityCoordinator coordinator,
        IPetPresentationScheduler scheduler)
        => new(coordinator, scheduler, NullLogger<PetActivityPresenter>.Instance);

    private static AgentActivityCoordinator CreateCoordinator(DesktopToolApprovalHandler handler)
        => new(handler, NullLogger<AgentActivityCoordinator>.Instance);

    private static AgentActivityContext CreateContext()
        => new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Pet architecture",
            "build",
            "Build",
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
