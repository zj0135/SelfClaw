using FluentAssertions;
using SelfClaw.Core.Models;
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

    private static TranscriptRenderSegment BuildSegment(
        string toolName,
        string argumentsJson,
        ToolSourceKind? sourceKind = null,
        string? displayName = null)
    {
        var now = DateTimeOffset.UtcNow;
        return TranscriptToolRunPresenter.BuildToolSegment(new ToolExecutionRecord(
            Guid.NewGuid(),
            Guid.NewGuid(),
            toolName,
            argumentsJson,
            ToolExecutionStatus.Completed,
            null,
            null,
            12,
            now,
            now,
            SourceKind: sourceKind,
            DisplayName: displayName));
    }
}
