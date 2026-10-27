using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime.Tooling;

namespace SelfClaw.Infrastructure.Agents.Direct.Context;

/// <summary>
/// The task plan as a system section. It only ever appears on a request whose summary already replaced the
/// history that carried the plan: replaying it every turn would rewrite the system prefix on every new
/// <c>todo_write</c> and invalidate the provider's prefix cache for a list the model wrote itself.
/// </summary>
internal static class TodoPlanSection
{
    private const string Heading = "[SelfClaw Plan]";

    private const string Note =
        "The task plan as it stood when the history above was summarized. A newer todo_write in the history supersedes it.";

    private const string CompletedMarker = "[x]";
    private const string InProgressMarker = "[~]";
    private const string PendingMarker = "[ ]";

    /// <summary>
    /// The section text for <paramref name="plan"/>, or null when there is nothing left to continue: an empty
    /// or fully completed plan is not worth a system section.
    /// </summary>
    public static string? Build(TodoPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.Items.Count == 0 || plan.Items.All(item => item.Status == TodoItemStatus.Completed))
        {
            return null;
        }

        var builder = new StringBuilder(Heading.Length + Note.Length + 1);
        builder.Append(Heading).Append('\n').Append(Note);
        foreach (var item in plan.Items)
        {
            builder.Append("\n- ").Append(Marker(item.Status)).Append(' ').Append(item.Content);
        }

        return builder.ToString();
    }

    /// <summary>
    /// The newest plan readable from these messages, in the order given. A call whose arguments no longer parse
    /// is skipped: replay elision replaces superseded snapshots with a placeholder, which is not a plan.
    /// </summary>
    public static TodoPlan? FindNewestPlan(IEnumerable<ChatMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        TodoPlan? newest = null;
        foreach (var message in messages)
        {
            foreach (var content in message.Contents)
            {
                if (content is not FunctionCallContent { Name: TodoPlanRules.ToolName } call ||
                    call.Arguments is null)
                {
                    continue;
                }

                if (TodoPlanRules.TryParse(JsonSerializer.Serialize(call.Arguments), out var plan) && plan is not null)
                {
                    newest = plan;
                }
            }
        }

        return newest;
    }

    private static string Marker(TodoItemStatus status) => status switch
    {
        TodoItemStatus.Completed => CompletedMarker,
        TodoItemStatus.InProgress => InProgressMarker,
        _ => PendingMarker
    };
}
