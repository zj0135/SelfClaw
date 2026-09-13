using SelfClaw.Core.Runtime;
using System.Text.Json;
using FluentAssertions;
using SelfClaw.Core.Models;
using SelfClaw.Desktop.Services.Activities;
using SelfClaw.Desktop.Services.Activities.Models;
using SelfClaw.Desktop.Services.Subagents.Models;
using SelfClaw.Desktop.Services.WebView;
using SelfClaw.Infrastructure.Agents.Subagents.Runtime;
using SelfClaw.Infrastructure.Options;
using SelfClaw.Tests.TestDoubles;

namespace SelfClaw.Tests.Desktop.Services.Activities;

public sealed class ActivityPanelProjectionTests
{
    [Fact]
    public async Task Escaped_unicode_large_blocks_and_metadata_fit_actual_wire_budget_and_remain_readable()
    {
        using var context = new SubagentActivityTestContext();
        var task = await context.CreateTaskAsync(claim: false);
        var page = await context.Service.ListAsync(new SubagentActivityQuery(task.ParentConversationId));
        var metadata = page.Activities.Single();
        var large = string.Concat(Enumerable.Repeat("\u4e2d\ud83d\ude42\"\\\n", 10000));
        var messageId = Guid.NewGuid();
        var message = new MessageRecord(messageId, task.ChildConversationId, MessageRole.Assistant, large,
            MessageStatus.Streaming, task.QueuedAtUtc, task.QueuedAtUtc,
            Segments: Enumerable.Range(0, 80).Select(index => new MessageSegmentRecord(messageId, index,
                index % 2 == 0 ? MessageSegmentKind.Thinking : MessageSegmentKind.Text, large, null)).ToArray());
        var detail = SubagentActivityContent.Create(metadata.Task.Status, large, message, []);
        var snapshot = new SubagentActivitySnapshot(metadata, detail, "live");
        var manyTasks = Enumerable.Range(0, 50).Select(_ => metadata with
        {
            Task = metadata.Task with { TaskId = Guid.NewGuid(), TaskPreview = large, ErrorMessage = large, DeliveryError = large }
        }).ToArray();
        page = page with { Activities = manyTasks };
        var query = new ActivityPanelQuery(Guid.NewGuid(), task.ParentConversationId, 1, DetailSelectionId: Guid.NewGuid(), TaskId: task.Id);
        var projection = new ActivityPanelProjection(StoragePathDefaults.CreateDefault());
        var state = projection.Build(query, page, snapshot, false) with { RequestId = new string('r', 128), Revision = long.MaxValue };
        var bytes = WebViewHostChannel.SerializeToUtf8Bytes(state);
        bytes.Length.Should().BeLessThanOrEqualTo(256 * 1024);
        var window = state.Sections.Single().Detail ?? throw new InvalidOperationException();
        window.TotalBlocks.Should().Be(80);
        window.BlockOffset.Should().Be(16);
        window.Message?.Segments.Should().HaveCount(64);
        window.Content.Should().Contain(reference => reference.IsTruncated);
        foreach (var segment in window.Message?.Segments ?? [])
        {
            segment.Markdown.Should().NotEndWith("\ud83d");
            var reference = window.Content.Single(item => item.SegmentId == segment.SegmentId);
            var remaining = SubagentActivityContent.Read(metadata.Task, detail, new SubagentContentQuery(task.ParentConversationId,
                task.Id, detail.ContentVersion, reference.ContentId, reference.PreviewCharacters));
            (segment.Markdown + remaining.Text).Should().Be(large[..(segment.Markdown.Length + remaining.Text.Length)]);
        }
        var earlier = projection.Build(query with { BlockOffset = 0, ContentVersion = detail.ContentVersion }, page, snapshot, false);
        earlier.Sections[0].Detail?.Message?.Segments.First().SegmentId.Should().Be($"{messageId:D}:thinking:0");
        var completed = snapshot with { Activity = metadata with { Task = metadata.Task with { Status = SubagentTaskStatus.Succeeded } } };
        var invalidated = projection.Build(query with { BlockOffset = 0, ContentVersion = "stale" }, page, completed, false);
        invalidated.Sections[0].Tasks.Should().HaveCount(50);
        invalidated.Sections[0].Counts.Should().Be(page.Counts);
        invalidated.Sections[0].Detail.Should().BeNull();
        invalidated.Sections[0].DetailError.Should().Be("content-changed");
        invalidated.Sections[0].SelectedTask?.TaskId.Should().Be(task.Id);
        invalidated.Sections[0].SelectedTask?.Status.Should().Be("succeeded");
        invalidated.Sections[0].Tasks.Should().NotContain(item => item.TaskId == task.Id);
        invalidated.StateError.Should().BeNull();
        using var document = JsonDocument.Parse(bytes);
        document.RootElement.GetProperty("sections")[0].GetProperty("tasks").GetArrayLength().Should().Be(50);
    }

    [Fact]
    public async Task Legacy_message_and_unplaced_tools_are_projected_without_invented_positions()
    {
        using var context = new SubagentActivityTestContext();
        var task = await context.CreateTaskAsync(claim: false);
        var page = await context.Service.ListAsync(new SubagentActivityQuery(task.ParentConversationId));
        var metadata = page.Activities.Single();
        var message = new MessageRecord(Guid.NewGuid(), task.ChildConversationId, MessageRole.Assistant, "legacy",
            MessageStatus.Completed, task.QueuedAtUtc, task.QueuedAtUtc);
        var tool = new ToolExecutionRecord(Guid.NewGuid(), task.ChildConversationId, "read_file", "{}",
            ToolExecutionStatus.Completed, "recorded", null, 3, task.QueuedAtUtc, task.QueuedAtUtc, MessageId: message.Id);
        var detail = SubagentActivityContent.Create(SubagentTaskStatus.Interrupted, "task", message, [tool]);
        var projection = new ActivityPanelProjection(StoragePathDefaults.CreateDefault());
        var result = projection.Build(new ActivityPanelQuery(Guid.NewGuid(), task.ParentConversationId, 1), page,
            new SubagentActivitySnapshot(metadata, detail, "persisted"), false).Sections[0].Detail;
        result?.HistoryCompleteness.Should().Be("partial");
        result?.Message?.Segments.Should().ContainSingle().Which.Markdown.Should().Be("legacy");
        result?.UnplacedTools.Should().ContainSingle().Which.SegmentId.Should().Be(tool.Id.ToString("D"));
        result?.Content.Should().Contain(reference => reference.ContentId == "final-text");
    }
}
