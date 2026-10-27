using SelfClaw.Infrastructure.Agents.Direct.Tools.Models;
using SelfClaw.Infrastructure.Agents.Direct.Capabilities;
using SelfClaw.Infrastructure.Agents.Direct.Tools;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.AI;
using SelfClaw.Core.Runtime;
using SelfClaw.Core.Runtime.Agent;
using SelfClaw.Core.Runtime.Tooling;
using SelfClaw.Tests.Infrastructure.Agents.Direct;

namespace SelfClaw.Tests.Infrastructure.Agents.Direct.Tools;

public sealed class TodoToolsetTests
{
    [Fact]
    public void CreateTools_exposes_one_unapproved_plan_tool()
    {
        var binding = new TodoToolset().CreateTools().Should().ContainSingle().Subject;

        binding.Tool.Name.Should().Be(TodoPlanRules.ToolName);
        binding.Tool.Description.Should().Contain("Update this conversation's task plan");
        binding.Tool.Description.Should().Contain("Statuses: pending, in_progress, completed.");
        binding.Descriptor.Kind.Should().Be(ToolCallKind.Plan);
        binding.Descriptor.SourceKind.Should().Be(ToolSourceKind.BuiltIn);
        binding.Descriptor.DisplayName.Should().Be("Update task plan");
        binding.RequiresApproval.Should().BeFalse();
    }

    [Fact]
    public void The_generated_schema_exposes_the_todos_array_at_the_top_level()
    {
        // The stored arguments_json is what every plan reader parses, so the list must not be nested under a
        // wrapper property.
        var schema = new TodoToolset().CreateTools().Single().Tool.JsonSchema;

        var properties = schema.GetProperty("properties");
        properties.GetProperty("todos").GetProperty("type")[0].GetString().Should().Be("array");
        var itemProperties = properties.GetProperty("todos").GetProperty("items").GetProperty("properties");
        itemProperties.TryGetProperty("content", out _).Should().BeTrue();
        itemProperties.TryGetProperty("status", out _).Should().BeTrue();
        itemProperties.TryGetProperty("activeForm", out _).Should().BeTrue();
    }

    [Fact]
    public async Task A_valid_list_reports_the_counts_the_summary_and_the_active_step()
    {
        var result = await InvokeAsync(Todos(
            ("Check the tool wiring", "completed", "Checking the tool wiring"),
            ("Add todo_write", "in_progress", "Adding todo_write"),
            ("Wire the transcript card", "pending", null)));

        result.Status.Should().Be(ToolCallStatus.Completed);
        result.Summary.Should().Be("Updated plan (1/3)");
        result.Content.GetProperty("total").GetInt32().Should().Be(3);
        result.Content.GetProperty("completed").GetInt32().Should().Be(1);
        result.Content.GetProperty("inProgress").GetInt32().Should().Be(1);
        result.Content.GetProperty("pending").GetInt32().Should().Be(1);
        result.Detail.Should().Be("Plan updated: 3 tasks (1 completed, 1 in progress). Current: Adding todo_write.");
    }

    [Fact]
    public async Task The_detail_falls_back_to_the_content_and_omits_the_active_step_when_none_is_running()
    {
        var result = await InvokeAsync(Todos(
            ("Check the tool wiring", "completed", null),
            ("Add todo_write", "pending", null)));

        result.Detail.Should().Be("Plan updated: 2 tasks (1 completed, 0 in progress).");
    }

    [Fact]
    public async Task An_empty_list_clears_the_plan()
    {
        var result = await InvokeAsync(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["todos"] = JsonSerializer.SerializeToElement(Array.Empty<object>())
        });

        result.Status.Should().Be(ToolCallStatus.Completed);
        result.Summary.Should().Be("Cleared plan.");
        result.Detail.Should().Be("Plan cleared.");
        result.Content.GetProperty("total").GetInt32().Should().Be(0);
        result.Content.GetProperty("completed").GetInt32().Should().Be(0);
        result.Content.GetProperty("inProgress").GetInt32().Should().Be(0);
        result.Content.GetProperty("pending").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task Two_in_progress_items_return_a_failed_result_instead_of_throwing()
    {
        var result = await InvokeAsync(Todos(
            ("First", "in_progress", null),
            ("Second", "in_progress", null)));

        result.Status.Should().Be(ToolCallStatus.Failed);
        result.Summary.Should().Be("Invalid todo list: At most one todo item can be in_progress.");
        result.Detail.Should().Be(result.Summary);
        result.Content.GetProperty("error").GetString().Should().Be("At most one todo item can be in_progress.");
    }

    [Fact]
    public async Task An_unknown_status_is_reported_with_the_value_that_was_sent()
    {
        var result = await InvokeAsync(Todos(("First", "in-progress", null)));

        result.Status.Should().Be(ToolCallStatus.Failed);
        result.Summary.Should().StartWith("Invalid todo list: ").And.Contain("in-progress");
    }

    [Fact]
    public async Task Duplicate_content_returns_a_failed_result()
    {
        var result = await InvokeAsync(Todos(
            ("Add todo_write", "completed", null),
            ("Add todo_write", "pending", null)));

        result.Status.Should().Be(ToolCallStatus.Failed);
        result.Summary.Should().Contain("unique");
    }

    private static Dictionary<string, object?> Todos(
        params (string Content, string Status, string? ActiveForm)[] items)
        => new(StringComparer.Ordinal)
        {
            ["todos"] = JsonSerializer.SerializeToElement(items.Select(item => new
            {
                content = item.Content,
                status = item.Status,
                activeForm = item.ActiveForm
            }))
        };

    private static async Task<DirectToolResult> InvokeAsync(Dictionary<string, object?> arguments)
    {
        var request = (DirectChatTurnRequest)DirectAgentChatRuntimeTests.CreateRequest(Guid.NewGuid());
        var bindings = DirectTurnCapabilityResolver.BindTools(request, new TodoToolset().CreateTools()).ToArray();
        var invoker = new DirectToolInvoker(
            request,
            bindings.ToDictionary(binding => binding.Tool.Name, StringComparer.Ordinal));

        var raw = await invoker.InvokeAsync(
            new FunctionCallContent("call-1", TodoPlanRules.ToolName, arguments), CancellationToken.None);
        return raw.Should().BeOfType<DirectToolResult>().Subject;
    }
}
