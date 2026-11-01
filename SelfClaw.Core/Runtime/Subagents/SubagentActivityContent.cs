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
}
