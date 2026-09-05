using FluentAssertions;
using SelfClaw.Core.Models;
using SelfClaw.Desktop.Services.Runtime;

namespace SelfClaw.Tests.Desktop.Services.Runtime;

public sealed class StreamingAssistantContentTests
{
    [Fact]
    public void Blocks_alternate_between_text_and_thinking()
    {
        var stream = CreateStream();

        stream.AppendText("first", DateTimeOffset.UtcNow);
        stream.AppendThinking("reason", DateTimeOffset.UtcNow);
        stream.AppendText("second", DateTimeOffset.UtcNow);

        stream.BuildSegments().Should().SatisfyRespectively(
            segment => segment.Should().BeEquivalentTo(new { Kind = MessageSegmentKind.Text, Text = "first" }),
            segment => segment.Should().BeEquivalentTo(new { Kind = MessageSegmentKind.Thinking, Text = "reason" }),
            segment => segment.Should().BeEquivalentTo(new { Kind = MessageSegmentKind.Text, Text = "second" }));
    }

    [Fact]
    public void Consecutive_same_kind_deltas_merge_into_one_block()
    {
        var stream = CreateStream();

        stream.AppendThinking("first ", DateTimeOffset.UtcNow);
        stream.AppendThinking("second", DateTimeOffset.UtcNow);

        stream.BuildSegments().Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new { Kind = MessageSegmentKind.Thinking, Text = "first second" });
    }

    [Fact]
    public void ToolCall_block_records_placement_by_ordinal()
    {
        var stream = CreateStream();
        var toolRunId = Guid.NewGuid();

        stream.AppendText("before", DateTimeOffset.UtcNow);
        stream.AppendToolCall(toolRunId, DateTimeOffset.UtcNow);
        stream.AppendText("after", DateTimeOffset.UtcNow);

        stream.BuildSegments().Should().SatisfyRespectively(
            segment => segment.Should().BeEquivalentTo(new { Kind = MessageSegmentKind.Text, Text = "before" }),
            segment => segment.Should().BeEquivalentTo(new { Kind = MessageSegmentKind.ToolCall, ToolRunId = toolRunId }),
            segment => segment.Should().BeEquivalentTo(new { Kind = MessageSegmentKind.Text, Text = "after" }));
    }

    [Fact]
    public void Derived_markdown_joins_text_blocks_and_excludes_thinking_and_tools()
    {
        var stream = CreateStream();

        stream.AppendThinking("hidden", DateTimeOffset.UtcNow);
        stream.AppendText("visible ", DateTimeOffset.UtcNow);
        stream.AppendToolCall(Guid.NewGuid(), DateTimeOffset.UtcNow);
        stream.AppendText("tail", DateTimeOffset.UtcNow);

        stream.BuildMarkdown().Should().Be("visible tail");
    }

    private static StreamingAssistantContent CreateStream()
    {
        var stream = new StreamingAssistantContent();
        stream.Initialize(Guid.NewGuid(), [], DateTimeOffset.UtcNow);
        return stream;
    }
}
