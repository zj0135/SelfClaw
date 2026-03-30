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
    }

    [Fact]
    public void Split_extracts_a_leading_think_block_from_visible_content()
    {
        var result = AssistantMessageSegmenter.Split("<think>\nstep 1\n\nstep 2\n</think>\n\nFinal answer");

        result.ThinkingMarkdown!.ReplaceLineEndings("\n").Should().Be("step 1\n\nstep 2");
        result.ContentMarkdown.Should().Be("Final answer");
    }

    [Fact]
    public void Split_combines_multiple_leading_think_blocks()
    {
        var result = AssistantMessageSegmenter.Split("<think>first</think>\n<think>second</think>\n\nAnswer");

        result.ThinkingMarkdown!.ReplaceLineEndings("\n").Should().Be("first\n\nsecond");
        result.ContentMarkdown.Should().Be("Answer");
    }

    [Fact]
    public void Split_treats_an_unclosed_think_block_as_streaming_reasoning()
    {
        var result = AssistantMessageSegmenter.Split("<think>\nworking...");

        result.ThinkingMarkdown!.ReplaceLineEndings("\n").Should().Be("working...");
        result.ContentMarkdown.Should().BeEmpty();
    }

    [Fact]
    public void Split_only_extracts_think_blocks_that_start_the_message()
    {
        var result = AssistantMessageSegmenter.Split("Example XML: <think>value</think>");

        result.ThinkingMarkdown.Should().BeNull();
        result.ContentMarkdown.Should().Be("Example XML: <think>value</think>");
    }

    [Fact]
    public void Split_extracts_think_blocks_after_bom_or_zero_width_prefix()
    {
        var result = AssistantMessageSegmenter.Split("\uFEFF\u200B<think>hidden</think>\n\nVisible answer");

        result.ThinkingMarkdown.Should().Be("hidden");
        result.ContentMarkdown.Should().Be("Visible answer");
    }
}

