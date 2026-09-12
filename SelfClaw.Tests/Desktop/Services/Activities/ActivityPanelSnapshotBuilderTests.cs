using FluentAssertions;
using SelfClaw.Desktop.Services.Activities;
using SelfClaw.Desktop.Services.Activities.Models;
using SelfClaw.Infrastructure.Options;
using SelfClaw.Tests.TestDoubles;

namespace SelfClaw.Tests.Desktop.Services.Activities;

public sealed class ActivityPanelSnapshotBuilderTests
{
    [Fact]
    public async Task Failed_and_empty_captures_are_ordered_with_successful_recovery()
    {
        using var activity = new SubagentActivityTestContext();
        var task = await activity.CreateTaskAsync(claim: false);
        var builder = new ActivityPanelSnapshotBuilder(activity.Service, StoragePaths.CreateDefault());
        var query = new ActivityPanelQuery(Guid.NewGuid(), task.ParentConversationId, 1, TaskId: task.Id);
        activity.Reader.AfterDetailReadAsync = () => throw new InvalidOperationException("temporary-read-failure");
        var failed = await builder.BuildAsync(query, CancellationToken.None);
        failed.StateError.Should().Be("temporary-read-failure");
        failed.Sections.Should().BeEmpty();
        failed.CaptureSequence.Should().BePositive("even a delayed error must be rejected after a newer capture is published");
        activity.Reader.AfterDetailReadAsync = null;
        var recovered = await builder.BuildAsync(query, CancellationToken.None);
        recovered.CaptureSequence.Should().BeGreaterThan(failed.CaptureSequence);
        recovered.StateError.Should().BeNull();
        recovered.Sections.Should().ContainSingle().Which.Detail?.TaskId.Should().Be(task.Id);
        var empty = await builder.BuildAsync(query with { ParentConversationId = null }, CancellationToken.None);
        empty.CaptureSequence.Should().BeGreaterThan(recovered.CaptureSequence);
        empty.Sections.Should().BeEmpty();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => builder.BuildAsync(query, cancelled.Token));
    }
}
