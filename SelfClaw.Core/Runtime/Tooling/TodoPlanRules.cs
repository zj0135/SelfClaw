using System.Text.Json;
using SelfClaw.Core.Models;

namespace SelfClaw.Core.Runtime.Tooling;

/// <summary>
/// The single source of the task-plan contract: the tool rejects exactly what the history reader refuses
/// to accept as a plan, so a write the transcript would not treat as a plan can never be stored as one.
/// </summary>
public static class TodoPlanRules
{
    public const string ToolName = "todo_write";
    public const int MaximumItems = 30;
    public const int MaximumContentLength = 200;
    public const int MaximumActiveFormLength = 100;

    /// <summary>A complete snapshot is replayed in full, so its size is capped by the prompt budget, not by taste.</summary>
    public const int MaximumArgumentsCharacters = 8 * 1024;

    private const string PendingStatusText = "pending";
    private const string InProgressStatusText = "in_progress";
    private const string CompletedStatusText = "completed";

    public static string? Validate(IReadOnlyList<TodoItem>? items)
    {
        if (items is null)
        {
            return "Provide the complete todo list; pass an empty array to clear the plan.";
        }

        if (items.Count > MaximumItems)
        {
            return $"A plan can hold at most {MaximumItems} items.";
        }

        var contents = new HashSet<string>(StringComparer.Ordinal);
        var inProgressCount = 0;
        foreach (var item in items)
        {
            var content = (item.Content ?? string.Empty).Trim();
            if (content.Length == 0)
            {
                return "Every todo item needs a non-empty content.";
            }

            if (content.Length > MaximumContentLength)
            {
                return $"Todo content must be at most {MaximumContentLength} characters.";
            }

            if (!contents.Add(content))
            {
                return $"Todo content must be unique; '{content}' appears more than once.";
            }

            if ((item.ActiveForm ?? string.Empty).Trim().Length > MaximumActiveFormLength)
            {
                return $"Todo activeForm must be at most {MaximumActiveFormLength} characters.";
            }

            if (item.Status == TodoItemStatus.InProgress)
            {
                inProgressCount++;
            }
        }

        return inProgressCount > 1 ? "At most one todo item can be in_progress." : null;
    }

    public static bool TryParseStatus(string? value, out TodoItemStatus status)
    {
        if (string.Equals(value, PendingStatusText, StringComparison.OrdinalIgnoreCase))
        {
            status = TodoItemStatus.Pending;
            return true;
        }

        if (string.Equals(value, InProgressStatusText, StringComparison.OrdinalIgnoreCase))
        {
            status = TodoItemStatus.InProgress;
            return true;
        }

        if (string.Equals(value, CompletedStatusText, StringComparison.OrdinalIgnoreCase))
        {
            status = TodoItemStatus.Completed;
            return true;
        }

        status = default;
        return false;
    }

    public static bool TryParse(string? argumentsJson, out TodoPlan? plan)
    {
        plan = null;
        if (string.IsNullOrWhiteSpace(argumentsJson) || argumentsJson.Length > MaximumArgumentsCharacters)
        {
            return false;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(argumentsJson);
        }
        catch (JsonException)
        {
            return false;
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !TryGetProperty(document.RootElement, "todos", out var todos) ||
                todos.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            var items = new List<TodoItem>(todos.GetArrayLength());
            foreach (var item in todos.EnumerateArray())
            {
                var parsed = ParseItem(item);
                if (parsed is null)
                {
                    return false;
                }

                items.Add(parsed);
            }

            if (Validate(items) is not null)
            {
                return false;
            }

            plan = new TodoPlan(items);
            return true;
        }
    }

    private static TodoItem? ParseItem(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object ||
            !TryGetProperty(item, "content", out var content) ||
            content.ValueKind != JsonValueKind.String ||
            !TryGetProperty(item, "status", out var status) ||
            status.ValueKind != JsonValueKind.String ||
            !TryParseStatus(status.GetString(), out var parsedStatus))
        {
            return null;
        }

        string? activeForm = null;
        if (TryGetProperty(item, "activeForm", out var activeFormElement) &&
            activeFormElement.ValueKind != JsonValueKind.Null)
        {
            if (activeFormElement.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var trimmed = (activeFormElement.GetString() ?? string.Empty).Trim();
            activeForm = trimmed.Length == 0 ? null : trimmed;
        }

        return new TodoItem((content.GetString() ?? string.Empty).Trim(), parsedStatus, activeForm);
    }

    // Stored arguments come from provider payloads, which are not guaranteed to match our casing exactly.
    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }
}
