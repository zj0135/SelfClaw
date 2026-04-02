using FluentAssertions;
using Microsoft.Extensions.AI;
using SelfClaw.Infrastructure.Agents;

namespace SelfClaw.Tests.Runtime;

public sealed class SelfClawAgentChatRuntimeTests
{
    [Fact]
    public void ExtractText_wraps_only_text_reasoning_content_in_think_tags()
    {
        var contents = new AIContent[]
        {
            new TextContent("Before "),
            new TextReasoningContent("internal"),
            new TextContent(" after")
        };

        SelfClawAgentChatRuntime.ExtractTextFromContents(contents).Should().Be("Before <think>internal</think> after");
    }

    [Fact]
    public void ExtractText_does_not_treat_arbitrary_reason_named_content_as_thinking()
    {
        var contents = new AIContent[]
        {
            new FakeReasonContent("should stay out of thinking"),
            new TextContent("Visible")
        };

        SelfClawAgentChatRuntime.ExtractTextFromContents(contents).Should().Be("Visible");
    }

    private sealed class FakeReasonContent : AIContent
    {
        public FakeReasonContent(string text)
        {
            Text = text;
        }

        public string Text { get; }
    }
}
