using FluentAssertions;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Core.Runtime.Agent;
using SelfClaw.Desktop.Services.Transcript;
using SelfClaw.Desktop.Services.Transcript.Views;

namespace SelfClaw.Tests.Desktop.Services.Transcript;

public sealed class TranscriptToolRunPresenterTests
{
    [Theory]
    [InlineData("read_file", "{\"relativePath\":\"src/Program.cs\"}", "Read src/Program.cs")]
    [InlineData("write_file", "{\"relativePath\":\"src/Program.cs\"}", "Write src/Program.cs")]
    [InlineData("edit_file", "{\"relativePath\":\"src/Program.cs\"}", "Edit src/Program.cs")]
    [InlineData("run_shell_command", "{\"command\":\"dotnet build\"}", "Run dotnet build")]
    [InlineData("list_files", "{}", "List workspace")]
    [InlineData("list_files", "{\"relativePath\":\"src\"}", "List src")]
    [InlineData("search_text", "{\"query\":\"token\"}", "Search \"token\"")]
    public void Keeps_the_tool_name_and_target_on_leaf_rows(string toolName, string argumentsJson, string expected)
    {
        BuildSegment(toolName, argumentsJson).Text.Should().Be(expected);
    }

    [Fact]
    public void Names_extension_tools_by_their_display_name()
    {
        var segment = BuildSegment("list_issues", "{}", ToolSourceKind.Mcp, "list_issues");

        segment.Text.Should().Be("List Issues");
    }

    [Fact]
    public void Describes_a_missing_request_in_chinese()
    {
        BuildSegment("read_file", "{}").DetailText.Should().Be("未提供文件路径。");
        BuildSegment("list_files", "{}").DetailText.Should().Be("路径：工作区根目录");
    }

    [Fact]
    public void Summarizes_an_AskUser_call_by_its_question_or_its_question_count()
    {
        BuildSegment(AskUserRules.ToolName, TwoQuestionArguments()).Text.Should().Be("Ask 2 questions");
        BuildSegment(AskUserRules.ToolName, OneQuestionArguments()).Text
            .Should().Be("Ask Which database should this service use?");
    }

    [Fact]
    public void An_unanswered_AskUser_card_lists_the_questions_and_their_options()
    {
        var segment = BuildSegment(
            AskUserRules.ToolName,
            TwoQuestionArguments(),
            status: ToolExecutionStatus.Running);

        segment.IsPending.Should().BeTrue();
        segment.DetailText.Should().Contain("1. Which database should this service use?")
            .And.Contain("1) PostgreSQL — Relational")
            .And.Contain("2) SQLite")
            .And.Contain("2. Which ORM?")
            .And.Contain("等待回答…");
    }

    [Fact]
    public void An_answered_AskUser_card_shows_the_stored_result_text()
    {
        const string result = "User answered:\n1. Which database should this service use? → PostgreSQL";
        var segment = BuildSegment(
            AskUserRules.ToolName,
            TwoQuestionArguments(),
            resultContent: result);

        segment.DetailText.Should().Be(result);
    }

    private static string OneQuestionArguments()
        => """
           {"questions":[{"question":"Which database should this service use?","header":"Database",
             "options":[{"label":"PostgreSQL","description":"Relational"},{"label":"SQLite"}]}]}
           """;

    private static string TwoQuestionArguments()
        => """
           {"questions":[
             {"question":"Which database should this service use?","header":"Database",
              "options":[{"label":"PostgreSQL","description":"Relational"},{"label":"SQLite"}]},
             {"question":"Which ORM?",
              "options":[{"label":"Entity Framework"},{"label":"Dapper"}]}]}
           """;

    private static TranscriptRenderSegment BuildSegment(
        string toolName,
        string argumentsJson,
        ToolSourceKind? sourceKind = null,
        string? displayName = null,
        ToolExecutionStatus status = ToolExecutionStatus.Completed,
        string? resultContent = null)
    {
        var now = DateTimeOffset.UtcNow;
        return TranscriptToolRunPresenter.BuildToolSegment(new ToolExecutionRecord(
            Guid.NewGuid(),
            Guid.NewGuid(),
            toolName,
            argumentsJson,
            status,
            null,
            null,
            12,
            now,
            now,
            SourceKind: sourceKind,
            DisplayName: displayName,
            ResultContent: resultContent));
    }
}
