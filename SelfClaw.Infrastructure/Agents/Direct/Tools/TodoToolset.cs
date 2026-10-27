using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.AI;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime.Agent;
using SelfClaw.Core.Runtime.Tooling;
using SelfClaw.Infrastructure.Agents.Direct.Tools.Models;

namespace SelfClaw.Infrastructure.Agents.Direct.Tools;

/// <summary>
/// The task-plan tool. It is stateless: the plan is whatever the last stored call says, so concurrent
/// conversations never share state and no repository is needed here.
/// </summary>
internal sealed class TodoToolset
{
    private const string ToolDescription =
        "Update this conversation's task plan. Pass the complete list every time; it replaces the previous plan. "
        + "Use it for multi-step work (3+ steps): before starting, when the active step changes, and right after a "
        + "step is verified complete. Keep exactly one item in_progress while work is underway. Mark a step "
        + "completed immediately when it is truly done; never mark work complete that you have not verified. "
        + "Skip it for a single trivial action, for answering questions, and for narration. Keep each content "
        + "short, specific and action-oriented (max 30 items, 200 characters each). Pass an empty list to clear. "
        + "Statuses: pending, in_progress, completed.";

    public IReadOnlyList<DirectToolBinding> CreateTools()
        =>
        [
            new(AIFunctionFactory.Create(TodoWriteAsync, new AIFunctionFactoryOptions
                {
                    Name = TodoPlanRules.ToolName,
                    Description = ToolDescription,
                    ExcludeResultSchema = true,
                    MarshalResult = (value, _, _) => value is DirectToolResult result
                        ? ValueTask.FromResult<object?>(result)
                        : throw new InvalidOperationException(
                            $"Tool '{TodoPlanRules.ToolName}' returned an invalid result.")
                }),
                new DirectToolDescriptor(ToolCallKind.Plan, ToolSourceKind.BuiltIn, DisplayName: "Update task plan"))
        ];

    // The list is the tool's single argument rather than a property of a wrapper record: a wrapper would put
    // the whole call one level deep in the generated schema, and the stored arguments_json - which every plan
    // reader parses - must stay {"todos": [...]}.
    private static Task<DirectToolResult> TodoWriteAsync(
        [Description(
            "The complete task list for this conversation. It replaces the previous plan; pass an empty array to clear it. "
            + "Order is display order and is never re-sorted by status.")]
        IReadOnlyList<TodoWriteItemArgument>? todos,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var items = new List<TodoItem>(todos?.Count ?? 0);
        foreach (var todo in todos ?? [])
        {
            if (!TodoPlanRules.TryParseStatus(todo.Status, out var status))
            {
                return Task.FromResult(Invalid(
                    $"Unknown status '{todo.Status}'. Expected pending, in_progress or completed."));
            }

            items.Add(new TodoItem(
                (todo.Content ?? string.Empty).Trim(),
                status,
                string.IsNullOrWhiteSpace(todo.ActiveForm) ? null : todo.ActiveForm.Trim()));
        }

        // A null list is a malformed call, not a clear: an empty array is how the model clears the plan.
        var failure = TodoPlanRules.Validate(todos is null ? null : items);
        return Task.FromResult(failure is null ? Describe(items) : Invalid(failure));
    }

    private static DirectToolResult Describe(IReadOnlyList<TodoItem> items)
    {
        var completed = items.Count(item => item.Status == TodoItemStatus.Completed);
        var inProgress = items.Count(item => item.Status == TodoItemStatus.InProgress);
        var pending = items.Count(item => item.Status == TodoItemStatus.Pending);
        var content = JsonSerializer.SerializeToElement(new
        {
            total = items.Count,
            completed,
            inProgress,
            pending
        });
        if (items.Count == 0)
        {
            return new DirectToolResult(ToolCallStatus.Completed, "Cleared plan.", content, "Plan cleared.");
        }

        var detail = $"Plan updated: {items.Count} tasks ({completed} completed, {inProgress} in progress).";
        var current = items.FirstOrDefault(item => item.Status == TodoItemStatus.InProgress);
        if (current is not null)
        {
            // The active step is what the user watches in the dock; the rest of the list stays out of the
            // replayed detail because it already lives in the call's arguments.
            detail += $" Current: {(string.IsNullOrEmpty(current.ActiveForm) ? current.Content : current.ActiveForm)}.";
        }

        return new DirectToolResult(
            ToolCallStatus.Completed, $"Updated plan ({completed}/{items.Count})", content, detail);
    }

    // A rejected list is a model error, not a runtime fault: it returns a result so the model can resend the
    // whole list without spending the turn's consecutive-fault budget.
    private static DirectToolResult Invalid(string reason)
        => new(ToolCallStatus.Failed, $"Invalid todo list: {reason}", JsonSerializer.SerializeToElement(new { error = reason }),
            $"Invalid todo list: {reason}");
}
