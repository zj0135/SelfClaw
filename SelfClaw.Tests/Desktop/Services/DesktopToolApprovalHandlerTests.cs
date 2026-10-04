using SelfClaw.Desktop.Services.Tools;
using FluentAssertions;
using SelfClaw.Core.Runtime;

namespace SelfClaw.Tests.Desktop.Services;

public sealed class DesktopToolApprovalHandlerTests
{
    [Theory]
    [InlineData(ToolApprovalDecision.Approved)]
    [InlineData(ToolApprovalDecision.Denied)]
    public async Task RequestApprovalAsync_completes_with_the_resolved_decision(ToolApprovalDecision decision)
    {
        var handler = new DesktopToolApprovalHandler();
        var request = CreateRequest();
        ToolApprovalRequest? published = null;
        handler.ApprovalRequested += value => published = value;

        var pending = handler.RequestApprovalAsync(request);

        published.Should().Be(request);
        handler.TryResolve(request.ToolExecutionId, decision).Should().BeTrue();
        (await pending).Should().Be(decision);
        handler.TryResolve(request.ToolExecutionId, decision == ToolApprovalDecision.Approved
            ? ToolApprovalDecision.Denied : ToolApprovalDecision.Approved).Should().BeFalse();
    }

    [Fact]
    public async Task Supersede_wins_exactly_once_and_a_late_click_cannot_execute()
    {
        var handler = new DesktopToolApprovalHandler();
        var request = CreateRequest();
        var completed = 0;
        handler.ApprovalCompleted += _ => completed++;
        var pending = handler.RequestApprovalAsync(request);

        handler.TryResolve(request.ToolExecutionId, ToolApprovalDecision.Superseded).Should().BeTrue();
        (await pending).Should().Be(ToolApprovalDecision.Superseded);
        handler.TryResolve(request.ToolExecutionId, ToolApprovalDecision.Approved).Should().BeFalse();
        completed.Should().Be(1);
    }

    [Fact]
    public async Task RequestApprovalAsync_cancellation_removes_the_pending_item()
    {
        var handler = new DesktopToolApprovalHandler();
        using var cancellationSource = new CancellationTokenSource();
        var request = CreateRequest();
        var pending = handler.RequestApprovalAsync(request, cancellationSource.Token);

        cancellationSource.Cancel();

        Func<Task> awaitPending = async () => await pending;
        await awaitPending.Should().ThrowAsync<OperationCanceledException>();
        handler.TryResolve(request.ToolExecutionId, ToolApprovalDecision.Approved).Should().BeFalse();
    }

    [Fact]
    public async Task RequestApprovalAsync_subscriber_failure_safely_rejects()
    {
        var handler = new DesktopToolApprovalHandler();
        var request = CreateRequest();
        handler.ApprovalRequested += _ => throw new InvalidOperationException("UI failed");

        (await handler.RequestApprovalAsync(request)).Should().Be(ToolApprovalDecision.Denied);
        handler.TryResolve(request.ToolExecutionId, ToolApprovalDecision.Approved).Should().BeFalse();
    }

    [Fact]
    public async Task RejectAll_rejects_every_pending_request()
    {
        var handler = new DesktopToolApprovalHandler();
        var first = handler.RequestApprovalAsync(CreateRequest());
        var second = handler.RequestApprovalAsync(CreateRequest());

        handler.RejectAll();

        (await first).Should().Be(ToolApprovalDecision.Denied);
        (await second).Should().Be(ToolApprovalDecision.Denied);
    }

    [Fact]
    public async Task RequestApprovalAsync_rejects_duplicate_execution_ids_without_disturbing_the_first_request()
    {
        var handler = new DesktopToolApprovalHandler();
        var request = CreateRequest();
        var first = handler.RequestApprovalAsync(request);

        Action duplicate = () => handler.RequestApprovalAsync(request);
        duplicate.Should().Throw<InvalidOperationException>().WithMessage("*already pending*");

        handler.TryResolve(request.ToolExecutionId, ToolApprovalDecision.Approved).Should().BeTrue();
        (await first).Should().Be(ToolApprovalDecision.Approved);
    }

    private static ToolApprovalRequest CreateRequest()
        => new(
            Guid.NewGuid(),
            "write_file",
            "Write file",
            "Writes a file in the workspace.",
            "{\"path\":\"README.md\"}",
            Guid.NewGuid());
}
