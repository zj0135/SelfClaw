using FluentAssertions;
using SelfClaw.Infrastructure.Tools;

namespace SelfClaw.Tests.Tools;

public sealed class AssistantMessageSegmenterTests
{
    [Fact]
    public void Split_returns_original_content_when_no_think_block_exists()
    {
        var result = AssistantMessageSegmenter.Split("Final answer");

        result.ContentMarkdown.Should().Be("Final answer");
        result.ThinkingMarkdown.Should().BeNull();
        result.Segments.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new AssistantMessageSegment(AssistantMessageSegmentKind.Content, "Final answer"));
    }

    [Fact]
    public void Split_extracts_a_leading_think_block_from_visible_content()
    {
        var result = AssistantMessageSegmenter.Split("<think>\nstep 1\n\nstep 2\n</think>\n\nFinal answer");

        result.ThinkingMarkdown!.ReplaceLineEndings("\n").Should().Be("step 1\n\nstep 2");
        result.ContentMarkdown.Should().Be("Final answer");
        result.Segments.Should().Equal(
            new AssistantMessageSegment(AssistantMessageSegmentKind.Thinking, "step 1\n\nstep 2"),
            new AssistantMessageSegment(AssistantMessageSegmentKind.Content, "Final answer"));
    }

    [Fact]
    public void Split_combines_multiple_leading_think_blocks()
    {
        var result = AssistantMessageSegmenter.Split("<think>first</think>\n<think>second</think>\n\nAnswer");

        result.ThinkingMarkdown!.ReplaceLineEndings("\n").Should().Be("firstsecond");
        result.ContentMarkdown.Should().Be("Answer");
        result.Segments.Should().Equal(
            new AssistantMessageSegment(AssistantMessageSegmentKind.Thinking, "firstsecond"),
            new AssistantMessageSegment(AssistantMessageSegmentKind.Content, "Answer"));
    }

    [Fact]
    public void Split_preserves_interleaved_think_and_content_segments_in_order()
    {
        var result = AssistantMessageSegmenter.Split("<think>first</think>\n\nOne\n<think>second</think>\nTwo");

        result.ContentMarkdown.ReplaceLineEndings("\n").Should().Be("One\n\nTwo");
        result.Segments.Should().Equal(
            new AssistantMessageSegment(AssistantMessageSegmentKind.Thinking, "first"),
            new AssistantMessageSegment(AssistantMessageSegmentKind.Content, "One\n"),
            new AssistantMessageSegment(AssistantMessageSegmentKind.Thinking, "second"),
            new AssistantMessageSegment(AssistantMessageSegmentKind.Content, "Two"));
    }

    [Fact]
    public void Split_merges_consecutive_think_blocks_after_visible_content()
    {
        var result = AssistantMessageSegmenter.Split("<think>first</think>\n\nOne\n<think>second</think>\n<think>third</think>\nTwo");

        result.Segments.Should().Equal(
            new AssistantMessageSegment(AssistantMessageSegmentKind.Thinking, "first"),
            new AssistantMessageSegment(AssistantMessageSegmentKind.Content, "One\n"),
            new AssistantMessageSegment(AssistantMessageSegmentKind.Thinking, "secondthird"),
            new AssistantMessageSegment(AssistantMessageSegmentKind.Content, "Two"));
    }

    [Fact]
    public void Split_merges_all_thinking_before_first_content_into_one_segment()
    {
        var result = AssistantMessageSegmenter.Split("<think>first</think><think>second</think>\n\nOne\n<think>third</think>\nTwo");

        result.Segments.Should().Equal(
            new AssistantMessageSegment(AssistantMessageSegmentKind.Thinking, "firstsecond"),
            new AssistantMessageSegment(AssistantMessageSegmentKind.Content, "One\n"),
            new AssistantMessageSegment(AssistantMessageSegmentKind.Thinking, "third"),
            new AssistantMessageSegment(AssistantMessageSegmentKind.Content, "Two"));
    }

    [Fact]
    public void Split_merges_pending_leading_thinking_into_one_streaming_segment()
    {
        var result = AssistantMessageSegmenter.Split("<think>first</think><think>second");

        result.Segments.Should().Equal(
            new AssistantMessageSegment(AssistantMessageSegmentKind.Thinking, "firstsecond", true));
    }

    [Fact]
    public void Split_compacts_excessive_blank_lines_inside_thinking_segments()
    {
        var result = AssistantMessageSegmenter.Split("<think>first\n\n\n\nsecond\n   \nthird</think>\n\nAnswer");

        result.Segments.Should().Equal(
            new AssistantMessageSegment(AssistantMessageSegmentKind.Thinking, "first\n\nsecond\n\nthird"),
            new AssistantMessageSegment(AssistantMessageSegmentKind.Content, "Answer"));
    }

    [Fact]
    public void Split_reconstructs_tokenized_adjacent_thinking_into_compact_text()
    {
        var result = AssistantMessageSegmenter.Split("<think>我</think><think>们</think><think>多</think><think>次</think><think>得</think><think>到</think>");

        result.Segments.Should().Equal(
            new AssistantMessageSegment(AssistantMessageSegmentKind.Thinking, "我们多次得到"));
    }

    [Fact]
    public void Split_treats_an_unclosed_think_block_as_streaming_reasoning()
    {
        var result = AssistantMessageSegmenter.Split("<think>\nworking...");

        result.ThinkingMarkdown!.ReplaceLineEndings("\n").Should().Be("working...");
        result.ContentMarkdown.Should().BeEmpty();
        result.Segments.Should().Equal(
            new AssistantMessageSegment(AssistantMessageSegmentKind.Thinking, "working...", true));
    }

    [Fact]
    public void Split_only_extracts_think_blocks_that_start_the_message()
    {
        var result = AssistantMessageSegmenter.Split("Example XML: <think>value</think>");

        result.ThinkingMarkdown.Should().BeNull();
        result.ContentMarkdown.Should().Be("Example XML: <think>value</think>");
        result.Segments.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new AssistantMessageSegment(AssistantMessageSegmentKind.Content, "Example XML: <think>value</think>"));
    }

    [Fact]
    public void Split_extracts_think_blocks_after_bom_or_zero_width_prefix()
    {
        var result = AssistantMessageSegmenter.Split("\uFEFF\u200B<think>hidden</think>\n\nVisible answer");

        result.ThinkingMarkdown.Should().Be("hidden");
        result.ContentMarkdown.Should().Be("Visible answer");
        result.Segments.Should().Equal(
            new AssistantMessageSegment(AssistantMessageSegmentKind.Thinking, "hidden"),
            new AssistantMessageSegment(AssistantMessageSegmentKind.Content, "Visible answer"));
    }
}

