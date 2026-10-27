using SelfClaw.Core.Models;

namespace SelfClaw.Core.Runtime.Tooling;

/// <summary>
/// The single reader of "what is this conversation's current plan". Both the model-facing prompt (Infrastructure)
/// and the transcript projection (Desktop) resolve it from stored history, so the selection lives in Core rather
/// than being repeated per assembly.
/// </summary>
public static class TodoPlanHistory
{
    /// <summary>
    /// The newest completed <c>todo_write</c> whose arguments still parse. A rejected or unparseable call is
    /// skipped rather than treated as "no plan", so one bad snapshot can never wipe the plan a user is looking at.
    /// </summary>
    public static TodoPlanSnapshot? FindLatest(
        IReadOnlyList<MessageRecord> messages,
        IReadOnlyList<ToolExecutionRecord> toolRuns)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(toolRuns);
        // Most conversations never call the tool, and this runs on every streaming tick in the projection.
        if (!toolRuns.Any(run => run.ToolName == TodoPlanRules.ToolName))
        {
            return null;
        }

        var messagesById = new Dictionary<Guid, MessageRecord>();
        foreach (var message in messages)
        {
            messagesById.TryAdd(message.Id, message);
        }

        var candidates = new List<(long Sequence, long Ordinal, ToolExecutionRecord Run)>();
        foreach (var run in toolRuns)
        {
            if (run.ToolName != TodoPlanRules.ToolName || run.Status != ToolExecutionStatus.Completed)
            {
                continue;
            }

            if (run.MessageId is not Guid messageId || !messagesById.TryGetValue(messageId, out var message))
            {
                continue;
            }

            candidates.Add((message.Sequence, SegmentOrdinal(message, run.Id), run));
        }

        // Sequence plus segment ordinal is the only pair that orders two calls inside the same millisecond.
        foreach (var candidate in candidates
                     .OrderBy(item => item.Sequence)
                     .ThenBy(item => item.Ordinal)
                     .Reverse())
        {
            if (TodoPlanRules.TryParse(candidate.Run.ArgumentsJson, out var plan) && plan is not null)
            {
                return new TodoPlanSnapshot(plan, candidate.Run, candidate.Sequence);
            }
        }

        return null;
    }

    // A run whose call segment is gone still counts as the latest known plan; it just sorts last within its message.
    private static long SegmentOrdinal(MessageRecord message, Guid runId)
    {
        foreach (var segment in message.Segments ?? [])
        {
            if (segment.Kind == MessageSegmentKind.ToolCall && segment.ToolRunId == runId)
            {
                return segment.Ordinal;
            }
        }

        return long.MaxValue;
    }
}
