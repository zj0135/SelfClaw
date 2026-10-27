using FluentAssertions;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime.Tooling;
using SelfClaw.Desktop.Services.Transcript;

namespace SelfClaw.Tests.Desktop.Services.Transcript;

public sealed class TranscriptTodoPresenterTests
{
    private const string ThreeStepArguments = """
        {"todos":[
          {"content":"检查 Direct 工具装配","status":"completed","activeForm":"检查 Direct 工具装配"},
          {"content":"新增 todo_write 工具","status":"in_progress"},
          {"content":"投影到 transcript 卡片","status":"pending"}
        ]}
        """;

    [Fact]
    public void Builds_a_todo_segment_carrying_the_plan_as_wire_strings()
    {
        var toolRun = CreateRun(ThreeStepArguments, ToolExecutionStatus.Completed, "Updated plan (2/3)",
            "Plan updated: 3 tasks (2 completed, 1 in progress).");

        var segment = TranscriptTodoPresenter.BuildTodoSegment(toolRun)
                      ?? throw new InvalidOperationException("Missing todo segment.");

        segment.Kind.Should().Be("todo");
        segment.Markdown.Should().BeEmpty();
        segment.IsPending.Should().BeFalse();
        segment.Text.Should().Be("Updated plan (2/3)");
        segment.Status.Should().Be("completed");
        segment.SegmentId.Should().Be(toolRun.Id.ToString("D"));
        segment.DetailText.Should().Be("Plan updated: 3 tasks (2 completed, 1 in progress).");
        segment.ToolName.Should().Be(TodoPlanRules.ToolName);
        segment.Todo.Should().NotBeNull();
        segment.Todo!.Items.Select(item => (item.Content, item.Status)).Should().Equal(
            ("检查 Direct 工具装配", "completed"),
            ("新增 todo_write 工具", "in_progress"),
            ("投影到 transcript 卡片", "pending"));
        segment.Todo.Items[0].ActiveForm.Should().Be("检查 Direct 工具装配");
        segment.Todo.Items[1].ActiveForm.Should().BeNull();
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"todos":[{"content":"Step","status":"running"}]}""")]
    [InlineData("""{"todos":[{"content":"Step","status":"in_progress"},{"content":"Other","status":"in_progress"}]}""")]
    [InlineData("""{"items":[]}""")]
    public void Falls_back_to_the_generic_tool_card_when_the_arguments_are_not_a_plan(string argumentsJson)
        => TranscriptTodoPresenter.BuildTodoSegment(
            CreateRun(argumentsJson, ToolExecutionStatus.Completed, "Invalid todo list", "{ error }"))
            .Should().BeNull();

    [Fact]
    public void Keeps_an_empty_plan_as_a_todo_segment_so_the_card_can_say_it_was_cleared()
    {
        var segment = TranscriptTodoPresenter.BuildTodoSegment(
            CreateRun("""{"todos":[]}""", ToolExecutionStatus.Completed, "Cleared plan.", "Plan cleared."))
            ?? throw new InvalidOperationException("Missing todo segment.");

        segment.Kind.Should().Be("todo");
        segment.Todo.Should().NotBeNull();
        segment.Todo!.Items.Should().BeEmpty();
    }

    [Fact]
    public void Keeps_the_plan_on_a_failed_run_and_reports_the_failure_status()
    {
        var segment = TranscriptTodoPresenter.BuildTodoSegment(
            CreateRun(ThreeStepArguments, ToolExecutionStatus.Failed, "Invalid todo list: duplicate content",
                "Invalid todo list: duplicate content"))
            ?? throw new InvalidOperationException("Missing todo segment.");

        segment.Status.Should().Be("failed");
        segment.DetailText.Should().Be("Invalid todo list: duplicate content");
        segment.Todo.Should().NotBeNull();
        segment.Todo!.Items.Should().HaveCount(3);
    }

    [Fact]
    public void Marks_an_unfinished_run_as_pending_and_falls_back_to_a_default_title()
    {
        var segment = TranscriptTodoPresenter.BuildTodoSegment(
            CreateRun(ThreeStepArguments, ToolExecutionStatus.Running, null, null))
            ?? throw new InvalidOperationException("Missing todo segment.");

        segment.IsPending.Should().BeTrue();
        segment.Text.Should().Be("更新任务计划");
        segment.DetailText.Should().BeNull();
    }

    [Fact]
    public void Limits_the_detail_text_to_the_displayed_budget()
    {
        var segment = TranscriptTodoPresenter.BuildTodoSegment(
            CreateRun(ThreeStepArguments, ToolExecutionStatus.Completed, "Updated plan (2/3)",
                new string('x', TranscriptToolResultLimiter.MaximumDisplayedCharacters + 1_000)))
            ?? throw new InvalidOperationException("Missing todo segment.");

        segment.DetailText.Should().HaveLength(TranscriptToolResultLimiter.MaximumDisplayedCharacters);
    }

    private static ToolExecutionRecord CreateRun(string argumentsJson, ToolExecutionStatus status,
        string? resultSummary, string? resultContent)
    {
        var now = DateTimeOffset.UtcNow;
        return new ToolExecutionRecord(
            Guid.NewGuid(),
            Guid.NewGuid(),
            TodoPlanRules.ToolName,
            argumentsJson,
            status,
            resultSummary,
            "call-1",
            8,
            now,
            now,
            MessageId: Guid.NewGuid(),
            ResultContent: resultContent);
    }
}
