using SelfClaw.Core.Models;

namespace SelfClaw.Desktop.Services.Runtime;

internal static class ConversationTurnFinalizationBuilder
{
    internal static ConversationTurnCommit Build(RecordedTurnFinalizationRequest request, DateTimeOffset now)
    {
        var turn = request.Turn with
        {
            Status = request.Kind switch
            {
                TurnFinalizationKind.Succeeded => ConversationTurnStatus.Succeeded,
                TurnFinalizationKind.Failed => ConversationTurnStatus.Failed,
                TurnFinalizationKind.Cancelled => ConversationTurnStatus.Cancelled,
                TurnFinalizationKind.Truncated => ConversationTurnStatus.Truncated,
                TurnFinalizationKind.Blocked => ConversationTurnStatus.Blocked,
                TurnFinalizationKind.Interrupted => ConversationTurnStatus.Interrupted,
                _ => throw new ArgumentOutOfRangeException(nameof(request), request.Kind, "Unsupported turn outcome.")
            },
            CompletedAtUtc = now,
            ErrorMessage = request.ErrorMessage,
            Usage = request.Usage
        };
        var messages = request.AssistantMessage is { } message
            ? new[] { CompleteMessage(message, request, now) }
            : Array.Empty<MessageRecord>();
        return new ConversationTurnCommit(turn, messages, CompleteTools(request, now));
    }

    private static MessageRecord CompleteMessage(
        MessageRecord message,
        RecordedTurnFinalizationRequest request,
        DateTimeOffset now)
    {
        var segments = TerminalBlockAligner.Align(message.Id, message.Segments ?? [], request.FinalText);
        return message with
        {
            MarkdownContent = string.Concat(segments
                .Where(segment => segment.Kind == MessageSegmentKind.Text)
                .Select(segment => segment.Text ?? string.Empty)),
            Segments = segments,
            Status = request.Kind is TurnFinalizationKind.Failed or TurnFinalizationKind.Cancelled or TurnFinalizationKind.Interrupted
                ? MessageStatus.Interrupted
                : MessageStatus.Sealed,
            UpdatedAtUtc = now
        };
    }

    private static IReadOnlyList<ToolExecutionRecord> CompleteTools(
        RecordedTurnFinalizationRequest request,
        DateTimeOffset now)
    {
        var cancelled = request.Kind == TurnFinalizationKind.Cancelled;
        return request.ToolExecutions.Select(tool => tool.Status is ToolExecutionStatus.Running or ToolExecutionStatus.AwaitingApproval
            ? tool with
            {
                Status = cancelled ? ToolExecutionStatus.Cancelled : ToolExecutionStatus.Failed,
                ResultSummary = tool.ResultSummary ?? (cancelled
                    ? "Generation stopped."
                    : "The agent run ended before this tool call completed."),
                DurationMs = (now - tool.CreatedAtUtc).TotalMilliseconds,
                UpdatedAtUtc = now
            }
            : tool).ToArray();
    }
}
