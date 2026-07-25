using Microsoft.Extensions.Logging;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime.Agent;
using SelfClaw.Infrastructure.Tools.Transcript;

namespace SelfClaw.Desktop.Services.Runtime;

/// <summary>
/// Reduces one conversation turn's <see cref="AgentStreamEvent"/> stream into the transcript projection held by
/// <see cref="ConversationRuntimeState"/>: assistant text / thinking deltas, inline tool runs, usage, and the
/// success / failure / cancellation terminal. Tool starts and completions persist immediately; the assistant
/// message and any pending tools are finalized atomically through <see cref="DesktopTurnFinalizer"/>. The module
/// touches no WPF type — it signals the owner through <see cref="ConversationRuntimeState.RaiseTranscriptChanged"/>
/// (throttled for streaming, immediate for the terminal snapshot), so the event sequence can be verified without a
/// window. Direct and CLI turns share this projection because both arrive as the same unified events.
/// </summary>
public sealed class ConversationTurnEngine
{
    private readonly IConversationRepository _conversationRepository;
    private readonly DesktopTurnFinalizer _turnFinalizer;
    private readonly ILogger<ConversationTurnEngine> _logger;

    public ConversationTurnEngine(
        IConversationRepository conversationRepository,
        DesktopTurnFinalizer turnFinalizer,
        ILogger<ConversationTurnEngine> logger)
    {
        _conversationRepository = conversationRepository;
        _turnFinalizer = turnFinalizer;
        _logger = logger;
    }

    /// <summary>
    /// Surfaces the streaming assistant placeholder before the first stream event. CLI process startup can take
    /// seconds before <see cref="RunStartedEvent"/>, and the transcript would otherwise show only the user message.
    /// </summary>
    internal void BeginAssistantMessage(ConversationRuntimeState session, AgentTurnState turn)
        => EnsureAssistantMessage(session, turn);

    /// <summary>Applies one stream event to the turn's transcript state, persisting tool runs as they arrive.</summary>
    internal async Task ApplyEventAsync(
        ConversationRuntimeState session,
        AgentTurnState turn,
        AgentStreamEvent streamEvent,
        CancellationToken cancellationToken)
    {
        switch (streamEvent)
        {
            case RunStartedEvent:
                EnsureAssistantMessage(session, turn);
                break;

            case AssistantTextDeltaEvent textDelta:
                EnsureAssistantMessage(session, turn);
                if (session.ApplyAssistantDelta(turn.AssistantMessageId, textDelta.Delta))
                {
                    session.RaiseTranscriptChanged(false);
                }

                break;

            case AssistantThinkingDeltaEvent thinkingDelta:
                EnsureAssistantMessage(session, turn);
                if (session.ApplyAssistantDelta(
                        turn.AssistantMessageId,
                        AssistantMessageSegmenter.WrapThinking(thinkingDelta.Delta)))
                {
                    session.RaiseTranscriptChanged(false);
                }

                break;

            case ToolCallStartedEvent toolStarted:
                EnsureAssistantMessage(session, turn);
                await StartToolRunAsync(session, turn, toolStarted, cancellationToken);
                break;

            case ToolCallCompletedEvent toolCompleted:
                await CompleteToolRunAsync(session, turn, toolCompleted, cancellationToken);
                break;

            case UsageReportedEvent usage:
                turn.InputTokens = usage.InputTokens ?? turn.InputTokens;
                turn.OutputTokens = usage.OutputTokens ?? turn.OutputTokens;
                break;

            case RunStatusEvent runStatus:
                EnsureAssistantMessage(session, turn);
                session.ActivityText = MapRunStatusText(runStatus.Status);
                session.RaiseTranscriptChanged(false);
                break;

            case RunCompletedEvent completed:
                await CompleteAssistantTurnAsync(session, turn, completed);
                break;

            // RawOutputEvent / PermissionRequestedEvent carry no transcript state in v1.
            default:
                break;
        }
    }

    /// <summary>
    /// Finalizes a turn interrupted by user cancellation or a consumer-side failure (the runtime never delivered a
    /// terminal event). Marks the assistant message cancelled / failed and closes any still-running tool.
    /// </summary>
    internal Task FinalizeInterruptedAsync(
        ConversationRuntimeState session,
        AgentTurnState turn,
        TurnFinalizationKind kind,
        string errorMessage)
    {
        EnsureAssistantMessage(session, turn);
        var existing = session.Messages.First(item => item.Id == turn.AssistantMessageId);
        return FinalizeTurnAsync(session, turn, existing, kind, finalText: null, errorMessage);
    }

    /// <summary>
    /// Creates the streaming assistant message the turn writes into, once, on the first event that needs it.
    /// Tracked in <see cref="ConversationRuntimeState.ActiveMessageIds"/> so an exception or cancellation mid-turn
    /// finalizes it through the same terminal-state path.
    /// </summary>
    private static void EnsureAssistantMessage(ConversationRuntimeState session, AgentTurnState turn)
    {
        if (turn.MessageCreated)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var message = new MessageRecord(
            turn.AssistantMessageId,
            session.ConversationId,
            MessageRole.Assistant,
            string.Empty,
            MessageStatus.Streaming,
            now,
            now,
            turn.AgentId,
            turn.AgentName,
            turn.AgentRole);

        session.ActiveMessageIds.Add(turn.AssistantMessageId);
        session.ReplaceMessage(message);
        turn.MessageCreated = true;
        session.RaiseTranscriptChanged(false);
    }

    private async Task StartToolRunAsync(
        ConversationRuntimeState session,
        AgentTurnState turn,
        ToolCallStartedEvent toolStarted,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var record = new ToolExecutionRecord(
            Id: Guid.NewGuid(),
            ConversationId: session.ConversationId,
            ToolName: toolStarted.ToolName,
            ArgumentsJson: string.IsNullOrWhiteSpace(toolStarted.ArgumentsJson) ? "{}" : toolStarted.ArgumentsJson,
            Status: ToolExecutionStatus.Running,
            ResultSummary: null,
            CorrelationId: toolStarted.ToolCallId,
            DurationMs: null,
            CreatedAtUtc: now,
            UpdatedAtUtc: now,
            MessageId: turn.AssistantMessageId,
            AfterSegmentIndex: null);

        var anchored = session.CaptureToolRunAnchor(record);
        turn.ToolRunsByCallId[toolStarted.ToolCallId] = anchored;
        session.UpsertToolRun(anchored);
        await _conversationRepository.UpsertToolExecutionAsync(anchored, cancellationToken);
        session.RaiseTranscriptChanged(false);
    }

    private async Task CompleteToolRunAsync(
        ConversationRuntimeState session,
        AgentTurnState turn,
        ToolCallCompletedEvent toolCompleted,
        CancellationToken cancellationToken)
    {
        if (!turn.ToolRunsByCallId.TryGetValue(toolCompleted.ToolCallId, out var startedRecord))
        {
            // A completion without a matching start (e.g. a result line for an unseen call) has nothing to anchor.
            return;
        }

        var updated = startedRecord with
        {
            Status = MapToolStatus(toolCompleted.Status),
            ResultSummary = toolCompleted.ResultSummary ?? startedRecord.ResultSummary,
            ResultContent = toolCompleted.ResultContent ?? startedRecord.ResultContent,
            DurationMs = (DateTimeOffset.UtcNow - startedRecord.CreatedAtUtc).TotalMilliseconds,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };

        var anchored = session.CaptureToolRunAnchor(updated);
        turn.ToolRunsByCallId[toolCompleted.ToolCallId] = anchored;
        session.UpsertToolRun(anchored);
        await _conversationRepository.UpsertToolExecutionAsync(anchored, cancellationToken);
        session.RaiseTranscriptChanged(false);
    }

    private async Task CompleteAssistantTurnAsync(
        ConversationRuntimeState session,
        AgentTurnState turn,
        RunCompletedEvent completed)
    {
        EnsureAssistantMessage(session, turn);
        var existing = session.Messages.FirstOrDefault(item => item.Id == turn.AssistantMessageId);
        if (existing is null)
        {
            return;
        }

        var kind = completed.Status == RunCompletionStatus.Succeeded
            ? TurnFinalizationKind.Succeeded
            : TurnFinalizationKind.Failed;
        var errorMessage = kind == TurnFinalizationKind.Succeeded
            ? null
            : completed.ErrorMessage ?? "The agent run failed.";

        await FinalizeTurnAsync(session, turn, existing, kind, completed.FinalText, errorMessage);
    }

    private async Task FinalizeTurnAsync(
        ConversationRuntimeState session,
        AgentTurnState turn,
        MessageRecord existing,
        TurnFinalizationKind kind,
        string? finalText,
        string? errorMessage)
    {
        if (turn.Completed)
        {
            return;
        }

        turn.PendingFinalization ??= new DesktopTurnFinalizationRequest(
            existing,
            turn.ToolRunsByCallId.Values.ToArray(),
            kind,
            finalText,
            errorMessage,
            turn.InputTokens,
            turn.OutputTokens,
            turn.StartedAtUtc);
        TurnFinalization? finalization;
        try
        {
            finalization = await _turnFinalizer.FinalizeAsync(turn.PendingFinalization);
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "Failed to persist terminal state for turn {TurnId}.",
                turn.AssistantMessageId);
            return;
        }

        if (finalization is null)
        {
            turn.Completed = true;
            session.ActiveMessageIds.Remove(turn.AssistantMessageId);
            return;
        }

        session.ReplaceMessage(finalization.AssistantMessage);
        foreach (var toolExecution in finalization.ToolExecutions)
        {
            turn.ToolRunsByCallId[toolExecution.CorrelationId ?? toolExecution.Id.ToString("D")] = toolExecution;
            session.UpsertToolRun(toolExecution);
        }

        turn.Completed = true;
        session.ActiveMessageIds.Remove(turn.AssistantMessageId);
        session.RaiseTranscriptChanged(true);
    }

    private static ToolExecutionStatus MapToolStatus(ToolCallStatus status)
        => status switch
        {
            ToolCallStatus.Completed => ToolExecutionStatus.Completed,
            ToolCallStatus.Failed => ToolExecutionStatus.Failed,
            ToolCallStatus.Canceled => ToolExecutionStatus.Cancelled,
            _ => ToolExecutionStatus.Completed
        };

    /// <summary>Shown in the transcript's pending indicator while the turn has produced no content yet.</summary>
    private static string MapRunStatusText(AgentRunStatus status)
        => status switch
        {
            AgentRunStatus.Initializing => "正在初始化...",
            AgentRunStatus.Requesting => "正在请求...",
            AgentRunStatus.Thinking => "正在思考...",
            AgentRunStatus.Running => "正在执行...",
            _ => "准备中..."
        };
}
