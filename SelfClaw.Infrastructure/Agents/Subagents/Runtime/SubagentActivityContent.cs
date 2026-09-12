using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using SelfClaw.Core.Models;

namespace SelfClaw.Infrastructure.Agents.Subagents.Runtime;

internal static class SubagentActivityContent
{
    internal static SubagentActivityDetail CreateDetail(SubagentActivityTask task, string taskText,
        MessageRecord? message, IReadOnlyList<ToolExecutionRecord> tools)
    {
        var placedIds = (message?.Segments ?? []).Where(segment => segment.Kind == MessageSegmentKind.ToolCall)
            .Select(segment => segment.ToolRunId).ToHashSet();
        var unplaced = tools.Where(tool => !placedIds.Contains(tool.Id)).ToArray();
        var completeness = task.Status is SubagentTaskStatus.Queued or SubagentTaskStatus.Running
            ? SubagentHistoryCompleteness.Unknown
            : task.Status == SubagentTaskStatus.Interrupted || message?.Segments is not { Count: > 0 } || unplaced.Length > 0
                ? SubagentHistoryCompleteness.Partial
                : SubagentHistoryCompleteness.Complete;
        return new SubagentActivityDetail(task, taskText, message, tools, unplaced, CreateVersion(message, tools), completeness);
    }

    internal static string CreateVersion(MessageRecord? message, IReadOnlyList<ToolExecutionRecord> tools)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray();
            JsonSerializer.Serialize(writer, message);
            JsonSerializer.Serialize(writer, tools);
            writer.WriteEndArray();
        }

        return Convert.ToHexString(SHA256.HashData(stream.GetBuffer().AsSpan(0, checked((int)stream.Length))));
    }

    internal static SubagentContentPage Read(SubagentActivityDetail detail, SubagentContentQuery query)
    {
        ArgumentNullException.ThrowIfNull(detail);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(query.ContentId);
        if (detail.Task.TaskId != query.TaskId || detail.Task.ParentConversationId != query.ParentConversationId)
        {
            throw new SubagentActivityReadException("task-not-found");
        }

        if (!string.Equals(detail.ContentVersion, query.ContentVersion, StringComparison.Ordinal))
        {
            throw new SubagentActivityReadException("content-changed");
        }

        var text = ResolveContent(detail, query.ContentId);
        if (query.Offset < 0 || query.Offset > text.Length || query.MaximumCharacters is < 2 or > 8192 ||
            (query.Offset > 0 && query.Offset < text.Length &&
             char.IsHighSurrogate(text[query.Offset - 1]) && char.IsLowSurrogate(text[query.Offset])))
        {
            throw new SubagentActivityReadException("invalid-content-range");
        }

        // Even a JSON-escaped UTF-16 character occupies at most six bytes; leave room for the envelope.
        var end = query.Offset + Math.Min(query.MaximumCharacters, text.Length - query.Offset);
        if (end < text.Length && char.IsHighSurrogate(text[end - 1]) && char.IsLowSurrogate(text[end]))
        {
            end--;
        }

        return new SubagentContentPage(query.TaskId, detail.ContentVersion, query.ContentId,
            text[query.Offset..end], query.Offset, end < text.Length ? end : null, text.Length);
    }

    private static string ResolveContent(SubagentActivityDetail detail, string contentId)
    {
        if (contentId == "task-text")
        {
            return detail.TaskText;
        }

        if (contentId == "final-text")
        {
            return detail.Message?.MarkdownContent ?? string.Empty;
        }

        var parts = contentId.Split('/');
        if (parts is ["segment", var ordinalText] &&
            int.TryParse(ordinalText, NumberStyles.None, CultureInfo.InvariantCulture, out var ordinal))
        {
            var segment = detail.Message?.Segments?.FirstOrDefault(item => item.Ordinal == ordinal);
            if (segment is { Kind: MessageSegmentKind.Text or MessageSegmentKind.Thinking })
            {
                return segment.Text ?? string.Empty;
            }
        }
        else if (parts is ["tool", var toolId, var field] && Guid.TryParseExact(toolId, "D", out var id))
        {
            var tool = detail.ToolRuns.FirstOrDefault(item => item.Id == id);
            if (tool is not null && field is "arguments" or "result")
            {
                return field == "arguments" ? tool.ArgumentsJson : tool.ResultContent ?? tool.ResultSummary ?? string.Empty;
            }
        }

        throw new SubagentActivityReadException("content-not-found");
    }
}
