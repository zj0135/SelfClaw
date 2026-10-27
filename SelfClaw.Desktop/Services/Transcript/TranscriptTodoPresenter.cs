using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime.Tooling;
using SelfClaw.Desktop.Services.Transcript.Views;

namespace SelfClaw.Desktop.Services.Transcript;

internal static class TranscriptTodoPresenter
{
    /// <summary>Returns null when the stored arguments are not a valid plan, so the caller can fall back to the generic tool card.</summary>
    public static TranscriptRenderSegment? BuildTodoSegment(ToolExecutionRecord toolRun)
    {
        ArgumentNullException.ThrowIfNull(toolRun);
        if (!TodoPlanRules.TryParse(toolRun.ArgumentsJson, out var plan) || plan is null)
        {
            return null;
        }

        return new TranscriptRenderSegment(
            "todo",
            string.Empty,
            toolRun.Status is ToolExecutionStatus.Running or ToolExecutionStatus.AwaitingApproval,
            Text: toolRun.ResultSummary ?? "更新任务计划",
            Status: toolRun.Status.ToString().ToLowerInvariant(),
            SegmentId: toolRun.Id.ToString("D"),
            DetailText: LimitDetail(toolRun.ResultContent),
            ToolName: toolRun.ToolName,
            SourceKind: toolRun.SourceKind?.ToString().ToLowerInvariant(),
            SourceId: toolRun.SourceId,
            DisplayName: toolRun.DisplayName,
            Todo: BuildPlan(plan));
    }

    internal static TranscriptTodoPlan BuildPlan(TodoPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return new TranscriptTodoPlan(plan.Items
            .Select(item => new TranscriptTodoItem(item.Content, StatusText(item.Status), item.ActiveForm))
            .ToArray());
    }

    // The renderers compare these literals directly; the status never crosses the wire as an enum or a number.
    private static string StatusText(TodoItemStatus status)
        => status switch
        {
            TodoItemStatus.Completed => "completed",
            TodoItemStatus.InProgress => "in_progress",
            // Mirrors the renderer's normalization: an unknown status degrades to pending instead of blanking the row.
            _ => "pending"
        };

    private static string? LimitDetail(string? resultContent)
        => resultContent is null ? null : TranscriptToolResultLimiter.LimitDisplayed(resultContent);
}
