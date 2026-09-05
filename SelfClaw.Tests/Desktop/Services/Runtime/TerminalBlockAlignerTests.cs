using FluentAssertions;
using SelfClaw.Core.Models;
using SelfClaw.Desktop.Services.Runtime;

namespace SelfClaw.Tests.Desktop.Services.Runtime;

public sealed class TerminalBlockAlignerTests
{
    [Fact]
    public void Fast_path_keeps_streamed_blocks_when_final_text_matches()
    {
        var messageId = Guid.NewGuid();
        var toolRunId = Guid.NewGuid();
        var streamed = new[]
        {
            new MessageSegmentRecord(messageId, 0, MessageSegmentKind.Thinking, "plan", null),
            new MessageSegmentRecord(messageId, 1, MessageSegmentKind.Text, "answer", null),
            new MessageSegmentRecord(messageId, 2, MessageSegmentKind.ToolCall, null, toolRunId),
            new MessageSegmentRecord(messageId, 3, MessageSegmentKind.Text, "tail", null)
        };

        TerminalBlockAligner.Align(messageId, streamed, "answertail")
            .Should().BeEquivalentTo(streamed);
    }

    [Fact]
    public void Slow_path_maps_tool_offsets_onto_the_final_text()
    {
        var messageId = Guid.NewGuid();
        var firstToolRunId = Guid.NewGuid();
        var secondToolRunId = Guid.NewGuid();
        var streamed = new[]
        {
            new MessageSegmentRecord(messageId, 0, MessageSegmentKind.Thinking, "plan", null),
            new MessageSegmentRecord(messageId, 1, MessageSegmentKind.Text, "One", null),
            new MessageSegmentRecord(messageId, 2, MessageSegmentKind.ToolCall, null, firstToolRunId),
            new MessageSegmentRecord(messageId, 3, MessageSegmentKind.Text, "Two", null),
            new MessageSegmentRecord(messageId, 4, MessageSegmentKind.ToolCall, null, secondToolRunId),
            new MessageSegmentRecord(messageId, 5, MessageSegmentKind.Text, "Three", null)
        };

        var result = TerminalBlockAligner.Align(messageId, streamed, "OneTwoThree");

        result.Should().SatisfyRespectively(
            segment => segment.Should().BeEquivalentTo(new { Kind = MessageSegmentKind.Thinking, Text = "plan" }),
            segment => segment.Should().BeEquivalentTo(new { Kind = MessageSegmentKind.Text, Text = "One" }),
            segment => segment.Should().BeEquivalentTo(new { Kind = MessageSegmentKind.ToolCall, ToolRunId = firstToolRunId }),
            segment => segment.Should().BeEquivalentTo(new { Kind = MessageSegmentKind.Text, Text = "Two" }),
            segment => segment.Should().BeEquivalentTo(new { Kind = MessageSegmentKind.ToolCall, ToolRunId = secondToolRunId }),
            segment => segment.Should().BeEquivalentTo(new { Kind = MessageSegmentKind.Text, Text = "Three" }));
    }

    [Fact]
    public void Slow_path_survives_a_final_text_that_rewrites_everything()
    {
        var messageId = Guid.NewGuid();
        var toolRunId = Guid.NewGuid();
        var streamed = new[]
        {
            new MessageSegmentRecord(messageId, 0, MessageSegmentKind.Text, "draft", null),
            new MessageSegmentRecord(messageId, 1, MessageSegmentKind.ToolCall, null, toolRunId)
        };

        // The numeric offset is carried over verbatim (same semantics as the legacy anchor
        // offset mapping), so the tool lands at streamed offset 5 inside the rewritten text.
        var result = TerminalBlockAligner.Align(messageId, streamed, "completely rewritten");

        result.Should().SatisfyRespectively(
            segment => segment.Should().BeEquivalentTo(new { Kind = MessageSegmentKind.Text, Text = "compl" }),
            segment => segment.Should().BeEquivalentTo(new { Kind = MessageSegmentKind.ToolCall, ToolRunId = toolRunId }),
            segment => segment.Should().BeEquivalentTo(new { Kind = MessageSegmentKind.Text, Text = "etely rewritten" }));
    }

    [Fact]
    public void Null_final_text_keeps_the_streamed_blocks()
    {
        var messageId = Guid.NewGuid();
        var streamed = new[]
        {
            new MessageSegmentRecord(messageId, 0, MessageSegmentKind.Text, "partial", null)
        };

        TerminalBlockAligner.Align(messageId, streamed, null)
            .Should().BeEquivalentTo(streamed);
    }
}
