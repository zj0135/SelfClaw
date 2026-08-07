using FluentAssertions;
using SelfClaw.Desktop.Services;

namespace SelfClaw.Tests.Desktop.Services.Agents;

public sealed class AgentMarkdownDocumentParserTests
{
    [Fact]
    public void Parse_extracts_scalars_lists_and_normalized_body()
    {
        var parser = new AgentMarkdownDocumentParser();

        var document = parser.Parse(
            """
            ---
            name: "Reviewer"
            skills:
              - code-review
              - 'nested/reviewer'
            ---

            Review the delegated task.
            """);

        document.Scalars.Should().Contain("name", "Reviewer");
        document.Lists["skills"].Should().Equal("code-review", "nested/reviewer");
        document.Body.Should().Be("Review the delegated task.");
        document.Diagnostics.Should().BeEmpty();
    }

    [Fact]
    public void Parse_reports_syntax_diagnostics_without_interpreting_fields()
    {
        var parser = new AgentMarkdownDocumentParser();

        var document = parser.Parse(
            """
            ---
              - orphan
            malformed
            name: First
            name: Second
            ---
            Instructions
            """);

        document.Diagnostics.Should().HaveCount(3);
        document.Diagnostics.Should().Contain(item => item.Contains("not attached", StringComparison.Ordinal));
        document.Diagnostics.Should().Contain(item => item.Contains("malformed", StringComparison.Ordinal));
        document.Diagnostics.Should().Contain(item => item.Contains("duplicated", StringComparison.Ordinal));
        document.Scalars["name"].Should().Be("First");
    }

    [Theory]
    [InlineData("", "Definition file is empty.")]
    [InlineData("Instructions only", "Front matter is missing.")]
    [InlineData("---\nname: Reviewer", "Front matter is incomplete.")]
    public void Parse_reports_missing_or_incomplete_front_matter(string markdown, string diagnostic)
    {
        var document = new AgentMarkdownDocumentParser().Parse(markdown);

        document.Diagnostics.Should().ContainSingle().Which.Should().Be(diagnostic);
    }
}
