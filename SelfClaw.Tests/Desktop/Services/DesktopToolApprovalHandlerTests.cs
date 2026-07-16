using FluentAssertions;
using SelfClaw.Core.Runtime;
using SelfClaw.Desktop.Services;

namespace SelfClaw.Tests.Desktop.Services;

public sealed class DesktopToolApprovalHandlerTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RequestApprovalAsync_completes_with_the_resolved_decision(bool approved)
    {
        var handler = new DesktopToolApprovalHandler(TimeSpan.FromSeconds(5));
        var request = CreateRequest();
        ToolApprovalRequest? published = null;
        handler.ApprovalRequested += value => published = value;

        var pending = handler.RequestApprovalAsync(request);

        published.Should().Be(request);
        handler.TryResolve(request.ToolExecutionId, approved).Should().BeTrue();
        (await pending).Should().Be(approved);
        handler.TryResolve(request.ToolExecutionId, !approved).Should().BeFalse();
    }

    [Fact]
    public async Task RequestApprovalAsync_cancellation_removes_the_pending_item()
    {
        var handler = new DesktopToolApprovalHandler(TimeSpan.FromSeconds(5));
        using var cancellationSource = new CancellationTokenSource();
        var request = CreateRequest();
        var pending = handler.RequestApprovalAsync(request, cancellationSource.Token);

        cancellationSource.Cancel();

        Func<Task> awaitPending = async () => await pending;
        await awaitPending.Should().ThrowAsync<OperationCanceledException>();
        handler.TryResolve(request.ToolExecutionId, approved: true).Should().BeFalse();
    }

    [Fact]
    public async Task RequestApprovalAsync_timeout_rejects_and_publishes_expiration_once()
    {
        var handler = new DesktopToolApprovalHandler(TimeSpan.FromMilliseconds(25));
        var request = CreateRequest();
        var expired = new List<ToolApprovalRequest>();
        handler.ApprovalExpired += expired.Add;

        var result = await handler.RequestApprovalAsync(request).WaitAsync(TimeSpan.FromSeconds(2));

        result.Should().BeFalse();
        expired.Should().Equal(request);
        handler.TryResolve(request.ToolExecutionId, approved: true).Should().BeFalse();
    }

    [Fact]
    public async Task RequestApprovalAsync_subscriber_failure_safely_rejects()
    {
        var handler = new DesktopToolApprovalHandler(TimeSpan.FromSeconds(5));
        var request = CreateRequest();
        handler.ApprovalRequested += _ => throw new InvalidOperationException("UI failed");

        (await handler.RequestApprovalAsync(request)).Should().BeFalse();
        handler.TryResolve(request.ToolExecutionId, approved: true).Should().BeFalse();
    }

    [Fact]
    public async Task RejectAll_rejects_every_pending_request()
    {
        var handler = new DesktopToolApprovalHandler(TimeSpan.FromSeconds(5));
        var first = handler.RequestApprovalAsync(CreateRequest());
        var second = handler.RequestApprovalAsync(CreateRequest());

        handler.RejectAll();

        (await first).Should().BeFalse();
        (await second).Should().BeFalse();
    }

    [Fact]
    public async Task RequestApprovalAsync_rejects_duplicate_execution_ids_without_disturbing_the_first_request()
    {
        var handler = new DesktopToolApprovalHandler(TimeSpan.FromSeconds(5));
        var request = CreateRequest();
        var first = handler.RequestApprovalAsync(request);

        Action duplicate = () => handler.RequestApprovalAsync(request);
        duplicate.Should().Throw<InvalidOperationException>().WithMessage("*already pending*");

        handler.TryResolve(request.ToolExecutionId, approved: true).Should().BeTrue();
        (await first).Should().BeTrue();
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
