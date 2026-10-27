using System.Text.Json;
using FluentAssertions;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime.Tooling;

namespace SelfClaw.Tests.Core.Tooling;

public sealed class TodoPlanRulesTests
{
    [Fact]
    public void TryParse_reads_the_wire_shape_and_trims_every_label()
    {
        const string json = """
            {"todos":[
              {"content":"  Check the tool wiring  ","status":"completed","activeForm":"  Checking the tool wiring  "},
              {"content":" Add todo_write ","status":"in_progress","activeForm":"   "}
            ]}
            """;

        var parsed = TodoPlanRules.TryParse(json, out var plan);

        parsed.Should().BeTrue();
        Assert.NotNull(plan);
        plan.Items.Should().Equal(
            new TodoItem("Check the tool wiring", TodoItemStatus.Completed, "Checking the tool wiring"),
            new TodoItem("Add todo_write", TodoItemStatus.InProgress));
    }

    [Fact]
    public void TryParse_accepts_an_empty_list_as_a_cleared_plan()
    {
        TodoPlanRules.TryParse("""{"todos":[]}""", out var plan).Should().BeTrue();

        Assert.NotNull(plan);
        plan.Items.Should().BeEmpty();
        TodoPlanRules.Validate([]).Should().BeNull();
    }

    [Theory]
    [InlineData("pending", TodoItemStatus.Pending)]
    [InlineData("in_progress", TodoItemStatus.InProgress)]
    [InlineData("completed", TodoItemStatus.Completed)]
    [InlineData("PENDING", TodoItemStatus.Pending)]
    [InlineData("In_Progress", TodoItemStatus.InProgress)]
    [InlineData("Completed", TodoItemStatus.Completed)]
    public void TryParseStatus_accepts_the_three_statuses_case_insensitively(string value, TodoItemStatus expected)
    {
        TodoPlanRules.TryParseStatus(value, out var status).Should().BeTrue();
        status.Should().Be(expected);
    }

    [Theory]
    [InlineData("in-progress")]
    [InlineData("started")]
    [InlineData("")]
    [InlineData(null)]
    public void TryParseStatus_rejects_every_other_value(string? value)
        => TodoPlanRules.TryParseStatus(value, out _).Should().BeFalse();

    [Fact]
    public void Validate_rejects_a_missing_list_and_accepts_the_empty_one()
    {
        TodoPlanRules.Validate(null).Should().NotBeNull();
        TodoPlanRules.Validate([]).Should().BeNull();
    }

    [Fact]
    public void More_than_thirty_items_are_rejected_by_the_rule_and_by_the_parser()
    {
        var items = Enumerable.Range(1, TodoPlanRules.MaximumItems + 1)
            .Select(index => new TodoItem($"Step {index}", TodoItemStatus.Pending))
            .ToArray();

        TodoPlanRules.Validate(items).Should().Contain(TodoPlanRules.MaximumItems.ToString());
        TodoPlanRules.TryParse(CreateArguments(items), out var plan).Should().BeFalse();
        plan.Should().BeNull();
    }

    [Fact]
    public void Duplicate_content_is_rejected_after_trimming()
    {
        TodoItem[] items =
        [
            new("Add todo_write", TodoItemStatus.Completed),
            new("  Add todo_write  ", TodoItemStatus.Pending)
        ];

        TodoPlanRules.Validate(items).Should().Contain("unique");
        TodoPlanRules.TryParse(CreateArguments(items), out _).Should().BeFalse();
    }

    [Fact]
    public void More_than_one_in_progress_item_is_rejected_but_none_is_fine()
    {
        TodoItem[] twoInProgress =
        [
            new("First", TodoItemStatus.InProgress),
            new("Second", TodoItemStatus.InProgress)
        ];

        TodoPlanRules.Validate(twoInProgress).Should().Contain("in_progress");
        TodoPlanRules.TryParse(CreateArguments(twoInProgress), out _).Should().BeFalse();
        TodoPlanRules.Validate([new("First", TodoItemStatus.InProgress), new("Second", TodoItemStatus.Completed)])
            .Should().BeNull();
        TodoPlanRules.Validate([new("First", TodoItemStatus.Completed)]).Should().BeNull();
    }

    [Fact]
    public void Content_must_be_non_empty_and_within_the_length_limit()
    {
        var atLimit = new string('c', TodoPlanRules.MaximumContentLength);

        TodoPlanRules.Validate([new("   ", TodoItemStatus.Pending)]).Should().Contain("non-empty");
        TodoPlanRules.Validate([new(atLimit, TodoItemStatus.Pending)]).Should().BeNull();
        TodoPlanRules.Validate([new(atLimit + "c", TodoItemStatus.Pending)])
            .Should().Contain(TodoPlanRules.MaximumContentLength.ToString());
    }

    [Fact]
    public void Active_form_must_be_within_the_length_limit_and_blank_counts_as_absent()
    {
        var atLimit = new string('a', TodoPlanRules.MaximumActiveFormLength);

        TodoPlanRules.Validate([new("Step", TodoItemStatus.Pending, atLimit)]).Should().BeNull();
        TodoPlanRules.Validate([new("Step", TodoItemStatus.Pending, "   ")]).Should().BeNull();
        TodoPlanRules.Validate([new("Step", TodoItemStatus.Pending, atLimit + "a")])
            .Should().Contain(TodoPlanRules.MaximumActiveFormLength.ToString());
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("""{"todos":"none"}""")]
    [InlineData("""{"todos":[{"content":"a","status":"started"}]}""")]
    [InlineData("""{"todos":[{"content":"","status":"pending"}]}""")]
    [InlineData("""{"todos":[{"status":"pending"}]}""")]
    [InlineData("""{"todos":["plain string"]}""")]
    [InlineData("""{"items":[]}""")]
    [InlineData("")]
    [InlineData(null)]
    public void TryParse_rejects_malformed_arguments(string? argumentsJson)
    {
        TodoPlanRules.TryParse(argumentsJson, out var plan).Should().BeFalse();
        plan.Should().BeNull();
    }

    [Fact]
    public void TryParse_rejects_arguments_over_the_size_cap()
    {
        var oversized = $"{{\"todos\":[],\"padding\":\"{new string('x', TodoPlanRules.MaximumArgumentsCharacters)}\"}}";

        oversized.Length.Should().BeGreaterThan(TodoPlanRules.MaximumArgumentsCharacters);
        TodoPlanRules.TryParse(oversized, out _).Should().BeFalse();
        TodoPlanRules.TryParse("""{"todos":[]}""", out _).Should().BeTrue();
    }

    [Fact]
    public void A_maximal_thirty_item_plan_still_fits_the_argument_cap()
    {
        // The cap is derived from 30 items of 200 characters; if the two drift apart, a legal plan would be
        // rejected for its size alone.
        var items = Enumerable.Range(1, TodoPlanRules.MaximumItems)
            .Select(index => new TodoItem($"{index:00}{new string('c', TodoPlanRules.MaximumContentLength - 2)}",
                TodoItemStatus.Pending))
            .ToArray();
        var arguments = CreateArguments(items);

        arguments.Length.Should().BeLessThanOrEqualTo(TodoPlanRules.MaximumArgumentsCharacters);
        TodoPlanRules.TryParse(arguments, out var plan).Should().BeTrue();
        Assert.NotNull(plan);
        plan.Items.Should().HaveCount(TodoPlanRules.MaximumItems);
    }

    private static string CreateArguments(params TodoItem[] items)
        => JsonSerializer.Serialize(new
        {
            todos = items.Select(item => new
            {
                content = item.Content,
                status = StatusText(item.Status),
                activeForm = item.ActiveForm
            })
        });

    private static string StatusText(TodoItemStatus status)
        => status switch
        {
            TodoItemStatus.Pending => "pending",
            TodoItemStatus.InProgress => "in_progress",
            _ => "completed"
        };
}
