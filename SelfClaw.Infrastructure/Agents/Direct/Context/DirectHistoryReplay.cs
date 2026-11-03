using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Core.Runtime.Tooling;
using SelfClaw.Infrastructure.Agents.Direct.Context.Models;

namespace SelfClaw.Infrastructure.Agents.Direct.Context;

/// <summary>
/// Rebuilds the stored history as the units a request replays. One unit is exactly one stored message and never
/// splits a tool call from its result, which is what lets a compaction boundary fall between units. Counting
/// stays with <see cref="DirectPromptMeter"/>: this type only decides which messages form a unit.
/// </summary>
internal static class DirectHistoryReplay
{
    /// <summary>
    /// Replaces the result text of every <c>todo_write</c> superseded by a newer valid plan. Each call
    /// carries a full snapshot, so replaying all of them would pay for the same list on every turn.
    /// </summary>
    internal const string ElidedTodoResultText = "Plan superseded by a newer todo_write.";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>
    /// Assembles a prompt from units the caller already built and counted, so a turn builds its history
    /// once and shares it between counting, compaction planning and the request itself.
    /// </summary>
    internal static DirectPromptBuild Compose(
        IReadOnlyList<DirectPromptHistoryUnit> units,
        string? systemText,
        IReadOnlyList<ChatMessage> trailingProtectedMessages,
        ConversationCompactionRecord? compression)
    {
        ArgumentNullException.ThrowIfNull(units);
        ArgumentNullException.ThrowIfNull(trailingProtectedMessages);
        // Units at or before the boundary are represented by the summary; the caller supplies the protected
        // messages already ordered after them.
        var replay = compression is null
            ? units
            : units.Where(unit => unit.SourceSequence > compression.ThroughSequence).ToArray();
        return new DirectPromptBuild(systemText, replay, trailingProtectedMessages);
    }

    internal static IReadOnlyList<DirectPromptHistoryUnit> BuildUnits(
        IReadOnlyList<MessageRecord> messages,
        IReadOnlyList<ConversationTurnRecord> turns,
        IReadOnlyList<ToolExecutionRecord> toolExecutions,
        IReadOnlyDictionary<Guid, string> messageAdjustments,
        IReadOnlyDictionary<Guid, IReadOnlyList<AIContent>>? attachments = null)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(turns);
        ArgumentNullException.ThrowIfNull(toolExecutions);
        ArgumentNullException.ThrowIfNull(messageAdjustments);
        var turnsById = turns.ToDictionary(turn => turn.Id);
        return BuildUnitsCore(OrderAndValidateMessages(messages, turnsById), turnsById, toolExecutions,
            messageAdjustments, attachments);
    }

    private static List<DirectPromptHistoryUnit> BuildUnitsCore(
        IReadOnlyList<MessageRecord> orderedMessages,
        IReadOnlyDictionary<Guid, ConversationTurnRecord> turnsById,
        IReadOnlyList<ToolExecutionRecord> toolExecutions,
        IReadOnlyDictionary<Guid, string> messageAdjustments,
        IReadOnlyDictionary<Guid, IReadOnlyList<AIContent>>? attachments)
    {
        var runsById = new Dictionary<Guid, ToolExecutionRecord>();
        foreach (var run in toolExecutions)
        {
            runsById.TryAdd(run.Id, run);
        }

        // Only the newest valid plan replays its arguments in full; every older snapshot is elided below.
        var replayable = orderedMessages
            .Where(message => IsReplayable(message, turnsById[message.TurnId]))
            .ToArray();
        var keepFullTodoRunId = TodoPlanHistory.FindLatest(replayable, toolExecutions)?.Run.Id;
        var units = new List<DirectPromptHistoryUnit>();
        foreach (var message in replayable)
        {
            var markdown = messageAdjustments.GetValueOrDefault(message.Id) ?? message.MarkdownContent;
            var unit = message.Role == MessageRole.Assistant
                ? BuildAssistantUnit(message, runsById, keepFullTodoRunId)
                : BuildUserUnit(message, markdown, attachments?.GetValueOrDefault(message.Id));
            if (unit.Messages.Count == 0)
            {
                continue;
            }

            units.Add(unit);
        }

        return units;
    }

    private static IReadOnlyList<MessageRecord> OrderAndValidateMessages(
        IReadOnlyList<MessageRecord> messages,
        IReadOnlyDictionary<Guid, ConversationTurnRecord> turns)
    {
        var ordered = true;
        for (var index = 1; index < messages.Count; index++)
        {
            if (messages[index - 1].Sequence >= messages[index].Sequence)
            {
                ordered = false;
                break;
            }
        }

        var result = ordered ? messages : messages.OrderBy(message => message.Sequence).ToArray();
        long previousSequence = 0;
        foreach (var message in result)
        {
            if (message.TurnId == Guid.Empty || message.Sequence <= previousSequence ||
                !turns.TryGetValue(message.TurnId, out var turn) || turn.ConversationId != message.ConversationId)
            {
                throw new InvalidDataException("Conversation history requires explicit turn ownership and unique positive message sequences.");
            }

            previousSequence = message.Sequence;
        }

        return result;
    }

    // A failed, cancelled or restart-interrupted turn keeps the assistant fragment it already produced:
    // the tool units that finished before the interruption are real work the next turn has to see, and
    // BuildAssistantUnit already drops the calls whose tool never reached a terminal state. Only a
    // fragment that was never settled stays out, because its text is not a provider-complete answer.
    private static bool IsReplayable(MessageRecord message, ConversationTurnRecord turn)
    {
        if (turn.Status == ConversationTurnStatus.Blocked)
        {
            return false;
        }

        return message.Role switch
        {
            MessageRole.User => message.Status == MessageStatus.Sealed,
            MessageRole.Assistant => message.Status is MessageStatus.Sealed or MessageStatus.Interrupted,
            _ => false
        };
    }

    // Images come first so the model reads the attachment before the text that refers to it, and a message
    // with only images still has to reach the provider: an empty markdown no longer drops the unit.
    private static DirectPromptHistoryUnit BuildUserUnit(MessageRecord message, string? markdown,
        IReadOnlyList<AIContent>? images)
    {
        if (images is not { Count: > 0 })
        {
            return new DirectPromptHistoryUnit(
                string.IsNullOrEmpty(markdown) ? [] : [new ChatMessage(ChatRole.User, markdown)],
                message.Sequence);
        }

        var contents = new List<AIContent>(images.Count + 1);
        contents.AddRange(images);
        if (!string.IsNullOrEmpty(markdown))
        {
            contents.Add(new TextContent(markdown));
        }

        return new DirectPromptHistoryUnit([new ChatMessage(ChatRole.User, contents)], message.Sequence,
            ContainsAttachments: true);
    }

    // Stored reasoning has no provider signature and is transcript-only. Assistant history is
    // reconstructed exclusively from actual blocks, never from an old unstructured markdown copy.
    private static DirectPromptHistoryUnit BuildAssistantUnit(
        MessageRecord message,
        IReadOnlyDictionary<Guid, ToolExecutionRecord> runsById,
        Guid? keepFullTodoRunId)
    {
        var contents = new List<AIContent>();
        var replay = new List<ChatMessage>();
        foreach (var segment in (message.Segments ?? []).OrderBy(item => item.Ordinal))
        {
            if (segment.MessageId != message.Id)
            {
                throw new InvalidDataException("A replayed segment must belong to its message.");
            }

            switch (segment.Kind)
            {
                case MessageSegmentKind.Text when !string.IsNullOrWhiteSpace(segment.Text):
                    contents.Add(new TextContent(segment.Text));
                    break;
                case MessageSegmentKind.ToolCall when segment.ToolRunId is Guid toolRunId:
                    if (!runsById.TryGetValue(toolRunId, out var run) ||
                        run.MessageId != message.Id || run.ConversationId != message.ConversationId ||
                        run.Status is not (ToolExecutionStatus.Completed or ToolExecutionStatus.Failed or
                            ToolExecutionStatus.Cancelled))
                    {
                        break;
                    }

                    var callId = run.CorrelationId ?? run.Id.ToString("D");
                    var elided = run.ToolName == TodoPlanRules.ToolName && run.Id != keepFullTodoRunId;
                    contents.Add(new FunctionCallContent(callId, run.ToolName,
                        elided ? CreateElidedArguments() : ParseArguments(run.ArgumentsJson)));
                    replay.Add(new ChatMessage(ChatRole.Assistant, contents));
                    replay.Add(CreateToolResultMessage(callId, run, elided));
                    contents = [];
                    break;
            }
        }

        if (contents.Count > 0)
        {
            replay.Add(new ChatMessage(ChatRole.Assistant, contents));
        }

        return new DirectPromptHistoryUnit(replay, message.Sequence);
    }

    private static ChatMessage CreateToolResultMessage(string callId, ToolExecutionRecord run, bool elided)
    {
        var text = elided
            ? ElidedTodoResultText
            : run.ResultContent ?? run.ResultSummary ?? string.Empty;
        var result = new FunctionResultContent(callId, text);
        if (run.Status is ToolExecutionStatus.Failed or ToolExecutionStatus.Cancelled)
        {
            result.Exception = new InvalidOperationException(run.ResultSummary ?? run.Status.ToString());
        }

        return new ChatMessage(ChatRole.Tool, [result]);
    }

    // The call and its result must stay paired, so a superseded snapshot is shortened instead of removed.
    private static Dictionary<string, object?> CreateElidedArguments() =>
        new(StringComparer.Ordinal) { ["elided"] = true };

    private static Dictionary<string, object?> ParseArguments(string argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, object?>>(argumentsJson, SerializerOptions) ?? [];
        }
        catch (JsonException)
        {
            // Malformed stored arguments must not fail the turn; the call replays without arguments.
            return [];
        }
    }
}
