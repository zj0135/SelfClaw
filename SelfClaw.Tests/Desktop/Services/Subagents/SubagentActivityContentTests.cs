using System.Text;
using System.Text.Json;
using FluentAssertions;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Core.Runtime.Agent;
using SelfClaw.Tests.TestDoubles;

namespace SelfClaw.Tests.Desktop.Services.Subagents;

public sealed class SubagentActivityContentTests
{
    [Fact]
    public void A_notice_segment_is_readable_as_content()
    {
        var messageId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var turn = new ConversationTurnRecord(Guid.NewGuid(), Guid.NewGuid(), AgentExecutionMode.Direct,
            DirectTurnOrigin.Subagent, ConversationTurnStatus.Blocked, now, now);
        var message = new MessageRecord(
            messageId,
            turn.ConversationId,
            turn.Id,
            1,
            MessageRole.Assistant,
            string.Empty,
            MessageStatus.Sealed,
            now,
            now,
            Segments:
            [
                new MessageSegmentRecord(messageId, 0, MessageSegmentKind.Notice, "hook notice", null),
                new MessageSegmentRecord(messageId, 1, MessageSegmentKind.Text, "answer", null)
            ]);
        var snapshot = SubagentActivityContent.Create(SubagentTaskStatus.Failed, "task", turn, message, []);
        var task = new SubagentActivityTask(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), turn.ConversationId, turn.Id,
            "child", "Child", "task", SubagentTaskStatus.Failed, 1, null, null, null,
            now, now, now, null, null, null, null, null, null, 0, null);
        var query = new SubagentContentQuery(
            task.ParentConversationId, task.TaskId, snapshot.ContentVersion, "segment/0");

        var page = SubagentActivityContent.Read(task, snapshot, query);

        page.Text.Should().Be("hook notice");
    }

    [Fact]
    public void A_no_output_terminal_is_complete_and_turn_only_changes_invalidate_the_content_version()
    {
        var now = DateTimeOffset.UtcNow;
        var turn = new ConversationTurnRecord(Guid.NewGuid(), Guid.NewGuid(), AgentExecutionMode.Direct,
            DirectTurnOrigin.Subagent, ConversationTurnStatus.Running, now);
        var running = SubagentActivityContent.Create(SubagentTaskStatus.Running, "task", turn, null, []);
        var failed = SubagentActivityContent.Create(SubagentTaskStatus.Failed, "task", turn with
        {
            Status = ConversationTurnStatus.Failed,
            CompletedAtUtc = now,
            ErrorMessage = "No output",
            Usage = new TurnUsage(InputTokens: 3, OutputTokens: 4, TotalTokens: null)
        }, null, []);

        running.Message.Should().BeNull();
        failed.Message.Should().BeNull();
        failed.HistoryCompleteness.Should().Be(SubagentHistoryCompleteness.Complete);
        failed.ContentVersion.Should().NotBe(running.ContentVersion);
        failed.Turn?.Usage?.TotalTokens.Should().BeNull();
    }

    [Fact]
    public void Tools_must_reference_the_actual_assistant_not_the_logical_turn()
    {
        var now = DateTimeOffset.UtcNow;
        var turn = new ConversationTurnRecord(Guid.NewGuid(), Guid.NewGuid(), AgentExecutionMode.Direct,
            DirectTurnOrigin.Subagent, ConversationTurnStatus.Running, now);
        var message = new MessageRecord(Guid.NewGuid(), turn.ConversationId, turn.Id, 1,
            MessageRole.Assistant, "output", MessageStatus.Streaming, now, now);
        var tool = new ToolExecutionRecord(Guid.NewGuid(), turn.ConversationId, "read_file", "{}",
            ToolExecutionStatus.Running, null, null, null, now, now, MessageId: turn.Id);

        var create = () => SubagentActivityContent.Create(SubagentTaskStatus.Running, "task", turn, message, [tool]);

        create.Should().Throw<ArgumentException>().WithParameterName("tools");
    }

    [Fact]
    public async Task Content_pages_use_the_live_service_and_enforce_unicode_ranges_versions_and_parent_ownership()
    {
        using var context = new SubagentActivityTestContext();
        var task = await context.CreateTaskAsync();
        var runtime = new ControlledSubagentRuntime();
        var execution = context.CreateExecutor(task, runtime).ExecuteAsync(task, CancellationToken.None);
        await runtime.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var text = new string('x', 8191) + "\U0001F600\u4e2d\u6587" + new string('\u0001', 25000);
        await runtime.EmitAsync(new AssistantThinkingDeltaEvent("thinking", "analysis"));
        await runtime.EmitAsync(new AssistantTextDeltaEvent("text", text));
        await runtime.EmitAsync(new ToolCallStartedEvent("call", "read_file", "{}", ToolCallKind.Read, ToolSourceKind.BuiltIn));
        await runtime.EmitAsync(new ToolCallCompletedEvent("call", ToolCallStatus.Completed, "read", new string('t', 60000)));
        var snapshot = await context.Service.GetDetailAsync(task.ParentConversationId, task.Id)
            ?? throw new InvalidOperationException("Missing live detail.");
        snapshot.ContentOrigin.Should().Be("live");
        var query = new SubagentContentQuery(task.ParentConversationId, task.Id, snapshot.Content.ContentVersion, "segment/1");
        var restored = new StringBuilder();
        int? offset = 0;
        while (offset is int next)
        {
            var page = await context.Service.ReadContentAsync(query with { Offset = next })
                ?? throw new InvalidOperationException("Missing content page.");
            JsonSerializer.SerializeToUtf8Bytes(page).Length.Should().BeLessThanOrEqualTo(64 * 1024);
            new UTF8Encoding(false, true).GetBytes(page.Text).Should().NotBeEmpty();
            restored.Append(page.Text);
            offset = page.NextOffset;
        }

        restored.ToString().Should().Be(text);
        (await context.Service.ReadContentAsync(query with { ParentConversationId = Guid.NewGuid() })).Should().BeNull();
        var split = () => context.Service.ReadContentAsync(query with { Offset = 8192 });
        await split.Should().ThrowAsync<SubagentActivityReadException>().WithMessage("invalid-content-range");
        var unknownTool = () => context.Service.ReadContentAsync(query with { ContentId = $"tool/{Guid.NewGuid():D}/result" });
        await unknownTool.Should().ThrowAsync<SubagentActivityReadException>().WithMessage("content-not-found");
        var tool = snapshot.Content.ToolRuns.Single();
        var toolPage = await context.Service.ReadContentAsync(query with { ContentId = $"tool/{tool.Id:D}/result", Offset = 50000 });
        toolPage?.Text.Should().HaveLength(8192);
        toolPage?.TotalCharacters.Should().Be(60000);

        await runtime.EmitAsync(new RunCompletedEvent(RunCompletionStatus.Succeeded, text));
        await execution;
        var stale = () => context.Service.ReadContentAsync(query);
        await stale.Should().ThrowAsync<SubagentActivityReadException>().WithMessage("content-changed");
        var terminal = await context.Service.GetDetailAsync(task.ParentConversationId, task.Id)
            ?? throw new InvalidOperationException("Missing persisted detail.");
        terminal.ContentOrigin.Should().Be("persisted");
        terminal.Content.HistoryCompleteness.Should().Be(SubagentHistoryCompleteness.Complete);
        var persisted = await context.Service.ReadContentAsync(query with { ContentVersion = terminal.Content.ContentVersion });
        persisted?.Text.Should().Be(text[..8191]);
    }
}
