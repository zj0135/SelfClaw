using FluentAssertions;
using SelfClaw.Core.Runtime.Tooling;

namespace SelfClaw.Tests.Core.Runtime;

public sealed class ToolResultTextLimitsTests
{
    [Fact]
    public void Limit_leaves_short_and_missing_values_alone()
    {
        ToolResultTextLimits.Limit(null).Should().BeNull();
        ToolResultTextLimits.Limit("short").Should().Be("short");
        ToolResultTextLimits.Limit(new string('x', ToolResultTextLimits.MaximumCharacters))
            .Should().HaveLength(ToolResultTextLimits.MaximumCharacters);
    }

    [Fact]
    public void Limit_trims_to_the_limit_and_marks_the_cut()
    {
        var limited = ToolResultTextLimits.Limit(new string('x', ToolResultTextLimits.MaximumCharacters + 1));

        limited.Should().HaveLength(ToolResultTextLimits.MaximumCharacters);
        limited.Should().EndWith(ToolResultTextLimits.TruncationMarker);
        limited.Should().StartWith(new string('x', 10));
    }

    [Fact]
    public void Limit_is_idempotent()
    {
        var once = ToolResultTextLimits.Limit(new string('x', ToolResultTextLimits.MaximumCharacters + 1_000));

        // The tool boundary and the recorder both apply it; a second pass must not shorten anything again.
        ToolResultTextLimits.Limit(once).Should().Be(once);
    }
}
