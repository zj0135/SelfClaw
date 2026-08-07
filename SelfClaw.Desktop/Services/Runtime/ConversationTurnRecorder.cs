using Microsoft.Extensions.Logging;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime.Agent;
using SelfClaw.Infrastructure.Tools.Transcript;

namespace SelfClaw.Desktop.Services.Runtime;

internal sealed class ConversationTurnRecorder
{
    private readonly IConversationRepository _conversationRepository;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ConversationTurnRecorder> _logger;

    public ConversationTurnRecorder(
        IConversationRepository conversationRepository,
        ILogger<ConversationTurnRecorder> logger)
        : this(conversationRepository, TimeProvider.System, logger)
    {
    }

    internal ConversationTurnRecorder(
        IConversationRepository conversationRepository,
        TimeProvider timeProvider,
        ILogger<ConversationTurnRecorder> logger)
    {
        _conversationRepository = conversationRepository;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    internal void BeginTurn(ConversationRuntimeState session, AgentTurnState turn)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(turn);

        // CLI process startup can delay the first event, so surface the assistant placeholder immediately.
        EnsureAssistantMessage(session, turn);
    }

    internal async Task ApplyEventAsync(
        ConversationRuntimeState session,
        AgentTurnState turn,
        AgentStreamEvent streamEvent,
        IRecordedTurnCommitter committer,
        CancellationToken cancellationToken)
        => await ApplyEventCoreAsync(
            session,
            turn,
            streamEvent,
            committer,
            persistToolProgress: true,
            cancellationToken);

    internal async Task ApplyDetachedEventAsync(
        ConversationRuntimeState session,
        AgentTurnState turn,
        AgentStreamEvent streamEvent,
        IRecordedTurnCommitter committer,
        CancellationToken cancellationToken)
        => await ApplyEventCoreAsync(
            session,
            turn,
            streamEvent,
            committer,
            persistToolProgress: false,
            cancellationToken);

    private async Task ApplyEventCoreAsync(
        ConversationRuntimeState session,
        AgentTurnState turn,
        AgentStreamEvent streamEvent,
        IRecordedTurnCommitter committer,
        bool persistToolProgress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(turn);
        ArgumentNullException.ThrowIfNull(streamEvent);
        ArgumentNullException.ThrowIfNull(committer);

        switch (streamEvent)
        {
            case RunStartedEvent:
                EnsureAssistantMessage(session, turn);
                break;

            case AssistantTextDeltaEvent textDelta:
                EnsureAssistantMessage(session, turn);
                if (session.ApplyAssistantDelta(turn.TurnId, textDelta.Delta))
                {
                    session.RaiseTranscriptChanged(false);
                }

                break;

            case AssistantThinkingDeltaEvent thinkingDelta:
                EnsureAssistantMessage(session, turn);
                if (session.ApplyAssistantDelta(
                        turn.TurnId,
                        AssistantMessageSegmenter.WrapThinking(thinkingDelta.Delta)))
                {
                    session.RaiseTranscriptChanged(false);
                }

                break;

            case ToolCallStartedEvent toolStarted:
                EnsureAssistantMessage(session, turn);
                await StartToolRunAsync(
                    session,
                    turn,
                    toolStarted,
                    persistToolProgress,
                    cancellationToken);
                break;

            case ToolCallCompletedEvent toolCompleted:
                await CompleteToolRunAsync(
                    session,
                    turn,
                    toolCompleted,
                    persistToolProgress,
                    cancellationToken);
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
                await CompleteAssistantTurnAsync(session, turn, completed, committer);
                break;

            // RawOutputEvent / PermissionRequestedEvent carry no transcript state in v1.
            default:
                break;
        }
    }

    internal Task FinalizeInterruptedAsync(
        ConversationRuntimeState session,
        AgentTurnState turn,
        TurnFinalizationKind kind,
        string errorMessage,
        IRecordedTurnCommitter committer)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(turn);
        ArgumentNullException.ThrowIfNull(errorMessage);
        ArgumentNullException.ThrowIfNull(committer);

        EnsureAssistantMessage(session, turn);
        var existing = session.Messages.First(item => item.Id == turn.TurnId);
        return FinalizeTurnAsync(session, turn, existing, kind, finalText: null, errorMessage, committer);
    }

    private static void EnsureAssistantMessage(ConversationRuntimeState session, AgentTurnState turn)
    {
        if (turn.MessageCreated)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var message = new MessageRecord(
            turn.TurnId,
            session.ConversationId,
            MessageRole.Assistant,
            string.Empty,
            MessageStatus.Streaming,
            now,
            now,
            null,
            turn.AgentName,
            turn.AgentRole);

        session.ReplaceMessage(message);
        turn.MessageCreated = true;
        session.RaiseTranscriptChanged(false);
    }

    private async Task StartToolRunAsync(
        ConversationRuntimeState session,
        AgentTurnState turn,
        ToolCallStartedEvent toolStarted,
        bool persistProgress,
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
            MessageId: turn.TurnId,
            AfterSegmentIndex: null,
            SourceKind: toolStarted.SourceKind,
            SourceId: toolStarted.SourceId,
            DisplayName: toolStarted.DisplayName);

        var anchored = session.CaptureToolRunAnchor(record);
        turn.ToolRunsByCallId[toolStarted.ToolCallId] = anchored;
        session.UpsertToolRun(anchored);
        if (persistProgress)
        {
            await _conversationRepository.UpsertToolExecutionAsync(anchored, cancellationToken);
        }

        session.RaiseTranscriptChanged(false);
    }

    private async Task CompleteToolRunAsync(
        ConversationRuntimeState session,
        AgentTurnState turn,
        ToolCallCompletedEvent toolCompleted,
        bool persistProgress,
        CancellationToken cancellationToken)
    {
        if (!turn.ToolRunsByCallId.TryGetValue(toolCompleted.ToolCallId, out var startedRecord))
        {
            // A result for an unseen call has no stable transcript position to anchor to.
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
        if (persistProgress)
        {
            await _conversationRepository.UpsertToolExecutionAsync(anchored, cancellationToken);
        }

        session.RaiseTranscriptChanged(false);
    }

    private async Task CompleteAssistantTurnAsync(
        ConversationRuntimeState session,
        AgentTurnState turn,
        RunCompletedEvent completed,
        IRecordedTurnCommitter committer)
    {
        EnsureAssistantMessage(session, turn);
        var existing = session.Messages.FirstOrDefault(item => item.Id == turn.TurnId);
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

        await FinalizeTurnAsync(session, turn, existing, kind, completed.FinalText, errorMessage, committer);
    }

    private async Task FinalizeTurnAsync(
        ConversationRuntimeState session,
        AgentTurnState turn,
        MessageRecord existing,
        TurnFinalizationKind kind,
        string? finalText,
        string? errorMessage,
        IRecordedTurnCommitter committer)
    {
        if (turn.Completed)
        {
            return;
        }

        turn.PendingFinalization ??= new RecordedTurnFinalizationRequest(
            existing,
            turn.ToolRunsByCallId.Values.ToArray(),
            kind,
            finalText,
            errorMessage,
            turn.InputTokens,
            turn.OutputTokens,
            turn.StartedAtUtc);
        var finalization = CreateFinalization(turn.PendingFinalization);
        bool written;
        try
        {
            written = await committer.TryCommitAsync(new RecordedTurnCommit(
                finalization,
                turn.PendingFinalization.Kind,
                turn.PendingFinalization.FinalText,
                turn.PendingFinalization.ErrorMessage));
        }
        catch (OperationCanceledException exception)
        {
            ApplyUnpersistedTerminalFailure(session, turn, exception);
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "Failed to persist terminal state for turn {TurnId}.",
                turn.TurnId);
            ApplyUnpersistedTerminalFailure(session, turn, exception);
            throw;
        }

        if (!written)
        {
            await ReloadCommittedTurnAsync(session, turn);
            turn.Completed = true;
            return;
        }

        ApplyFinalization(session, turn, finalization, persisted: true);
    }

    private async Task ReloadCommittedTurnAsync(
        ConversationRuntimeState session,
        AgentTurnState turn)
    {
        var messages = await _conversationRepository.ListMessagesAsync(session.ConversationId);
        var persistedMessage = messages.FirstOrDefault(message => message.Id == turn.TurnId);
        if (persistedMessage is not null)
        {
            session.ReplaceMessage(persistedMessage);
        }

        var toolExecutions = await _conversationRepository.ListToolExecutionsAsync(session.ConversationId);
        foreach (var toolExecution in toolExecutions.Where(tool => tool.MessageId == turn.TurnId))
        {
            turn.ToolRunsByCallId[toolExecution.CorrelationId ?? toolExecution.Id.ToString("D")] = toolExecution;
            session.UpsertToolRun(toolExecution);
        }

        session.RaiseTranscriptChanged(true);
    }

    private TurnFinalization CreateFinalization(RecordedTurnFinalizationRequest request)
    {
        var now = _timeProvider.GetUtcNow();
        return new TurnFinalization(
            BuildAssistantMessage(request, now),
            BuildToolExecutions(request, now));
    }

    private void ApplyUnpersistedTerminalFailure(
        ConversationRuntimeState session,
        AgentTurnState turn,
        Exception exception)
    {
        var pending = turn.PendingFinalization
            ?? throw new InvalidOperationException("The turn has no pending terminal state.");
        var fallbackKind = pending.Kind == TurnFinalizationKind.Succeeded
            ? TurnFinalizationKind.Failed
            : pending.Kind;
        var fallbackError = pending.Kind == TurnFinalizationKind.Succeeded
            ? $"Failed to persist terminal state: {exception.Message}"
            : pending.ErrorMessage ?? exception.Message;
        turn.PendingFinalization = pending with
        {
            Kind = fallbackKind,
            FinalText = null,
            ErrorMessage = fallbackError
        };

        ApplyFinalization(
            session,
            turn,
            CreateFinalization(turn.PendingFinalization),
            persisted: false);
    }

    private static MessageRecord BuildAssistantMessage(
        RecordedTurnFinalizationRequest request,
        DateTimeOffset now)
    {
        var finalMarkdown = request.FinalText is null
            ? request.AssistantMessage.MarkdownContent
            : AssistantMessageSegmenter.MergeFinalMarkdown(
                request.FinalText,
                request.AssistantMessage.MarkdownContent);

        return request.AssistantMessage with
        {
            MarkdownContent = finalMarkdown,
            Status = request.Kind switch
            {
                TurnFinalizationKind.Succeeded => MessageStatus.Completed,
                TurnFinalizationKind.Failed => MessageStatus.Failed,
                TurnFinalizationKind.Cancelled => MessageStatus.Cancelled,
                _ => throw new ArgumentOutOfRangeException(nameof(request), request.Kind, "Unsupported turn outcome.")
            },
            InputTokens = request.InputTokens,
            OutputTokens = request.OutputTokens,
            DurationMs = (now - request.StartedAtUtc).TotalMilliseconds,
            ErrorMessage = request.ErrorMessage,
            UpdatedAtUtc = now
        };
    }

    private static IReadOnlyList<ToolExecutionRecord> BuildToolExecutions(
        RecordedTurnFinalizationRequest request,
        DateTimeOffset now)
    {
        var pendingStatus = request.Kind == TurnFinalizationKind.Cancelled
            ? ToolExecutionStatus.Cancelled
            : ToolExecutionStatus.Failed;
        var pendingSummary = request.Kind == TurnFinalizationKind.Cancelled
            ? "Generation stopped."
            : "The agent run ended before this tool call completed.";

        return request.ToolExecutions
            .Select(toolExecution => toolExecution.Status is ToolExecutionStatus.Running or ToolExecutionStatus.AwaitingApproval
                ? toolExecution with
                {
                    Status = pendingStatus,
                    ResultSummary = toolExecution.ResultSummary ?? pendingSummary,
                    DurationMs = (now - toolExecution.CreatedAtUtc).TotalMilliseconds,
                    UpdatedAtUtc = now
                }
                : toolExecution)
            .ToArray();
    }

    private static void ApplyFinalization(
        ConversationRuntimeState session,
        AgentTurnState turn,
        TurnFinalization finalization,
        bool persisted)
    {
        session.ReplaceMessage(finalization.AssistantMessage);
        foreach (var toolExecution in finalization.ToolExecutions)
        {
            turn.ToolRunsByCallId[toolExecution.CorrelationId ?? toolExecution.Id.ToString("D")] = toolExecution;
            session.UpsertToolRun(toolExecution);
        }

        if (persisted)
        {
            turn.Completed = true;
        }

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
