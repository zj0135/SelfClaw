using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using SelfClaw.Core.Models;

namespace SelfClaw.Core.Runtime;

public static class SubagentActivityContent
{
    public static SubagentContentSnapshot Create(SubagentTaskStatus status, string taskText,
        ConversationTurnRecord? turn, MessageRecord? message, IReadOnlyList<ToolExecutionRecord> tools)
    {
        ArgumentNullException.ThrowIfNull(taskText);
        ArgumentNullException.ThrowIfNull(tools);
        if (message is not null && (turn is null || message.TurnId != turn.Id || message.ConversationId != turn.ConversationId))
        {
            throw new ArgumentException("Activity output must belong to its explicit turn.", nameof(message));
        }

        if (tools.Any(tool => message is null || tool.MessageId != message.Id || tool.ConversationId != message.ConversationId))
        {
            throw new ArgumentException("Activity tools must be anchored to the actual output message.", nameof(tools));
        }

        var placedIds = (message?.Segments ?? []).Where(segment => segment.Kind == MessageSegmentKind.ToolCall)
            .Select(segment => segment.ToolRunId).ToHashSet();
        var unplaced = tools.Where(tool => !placedIds.Contains(tool.Id)).ToArray();
        var completeness = status is SubagentTaskStatus.Queued or SubagentTaskStatus.Running
            ? SubagentHistoryCompleteness.Unknown
            : turn is null || turn.Status == ConversationTurnStatus.Interrupted || unplaced.Length > 0
                ? SubagentHistoryCompleteness.Partial
                : SubagentHistoryCompleteness.Complete;
        return new SubagentContentSnapshot(taskText, turn, message, tools, unplaced, CreateVersion(turn, message, tools), completeness);
    }

    public static SubagentContentPage Read(SubagentActivityTask task, SubagentContentSnapshot detail, SubagentContentQuery query)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(detail);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(query.ContentId);
        if (task.TaskId != query.TaskId || task.ParentConversationId != query.ParentConversationId)
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

    private static string CreateVersion(ConversationTurnRecord? turn, MessageRecord? message, IReadOnlyList<ToolExecutionRecord> tools)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray();
            JsonSerializer.Serialize(writer, turn);
            JsonSerializer.Serialize(writer, message);
            JsonSerializer.Serialize(writer, tools);
            writer.WriteEndArray();
        }

        return Convert.ToHexString(SHA256.HashData(stream.GetBuffer().AsSpan(0, checked((int)stream.Length))));
    }

    private static string ResolveContent(SubagentContentSnapshot detail, string contentId)
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
            if (segment is { Kind: MessageSegmentKind.Text or MessageSegmentKind.Thinking or MessageSegmentKind.Notice })
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
