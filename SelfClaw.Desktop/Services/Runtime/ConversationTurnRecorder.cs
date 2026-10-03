using System.IO;
using Microsoft.Extensions.Logging;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Core.Runtime.Agent;
using SelfClaw.Desktop.Services.Transcript;

namespace SelfClaw.Desktop.Services.Runtime;

internal sealed class ConversationTurnRecorder
{
    private readonly IConversationRepository _conversations;
    private readonly IConversationTurnRepository _turns;
    private readonly IConversationInputRepository _inputs;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ConversationTurnRecorder> _logger;

    public ConversationTurnRecorder(
        IConversationRepository conversations,
        IConversationTurnRepository turns,
        IConversationInputRepository inputs,
        ILogger<ConversationTurnRecorder> logger)
        : this(conversations, turns, inputs, TimeProvider.System, logger)
    {
    }

    internal ConversationTurnRecorder(
        IConversationRepository conversations,
        IConversationTurnRepository turns,
        IConversationInputRepository inputs,
        TimeProvider timeProvider,
        ILogger<ConversationTurnRecorder> logger)
    {
        ArgumentNullException.ThrowIfNull(conversations);
        ArgumentNullException.ThrowIfNull(turns);
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        _conversations = conversations;
        _turns = turns;
        _inputs = inputs;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    internal void BeginTurn(ConversationRuntimeState session, AgentTurnState turn)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(turn);
        if (session.ConversationId != turn.Record.ConversationId)
            throw new InvalidDataException("The turn does not belong to this conversation.");
        session.ReplaceTurn(turn.Record);
        session.RaiseTranscriptChanged(false);
    }

    internal Task ApplyEventAsync(ConversationRuntimeState session, AgentTurnState turn,
        AgentStreamEvent streamEvent, IRecordedTurnCommitter committer, CancellationToken cancellationToken)
        => ApplyEventCoreAsync(session, turn, streamEvent, committer, persistProgress: true, cancellationToken);

    internal Task ApplyDetachedEventAsync(ConversationRuntimeState session, AgentTurnState turn,
        AgentStreamEvent streamEvent, IRecordedTurnCommitter committer, CancellationToken cancellationToken)
        => ApplyEventCoreAsync(session, turn, streamEvent, committer, persistProgress: false, cancellationToken);

    internal Task FinalizeInterruptedAsync(ConversationRuntimeState session, AgentTurnState turn,
        TurnFinalizationKind kind, string errorMessage, IRecordedTurnCommitter committer)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(turn);
        ArgumentNullException.ThrowIfNull(errorMessage);
        ArgumentNullException.ThrowIfNull(committer);
        return FinalizeTurnAsync(session, turn, kind, null, errorMessage, committer);
    }

    private async Task ApplyEventCoreAsync(ConversationRuntimeState session, AgentTurnState turn,
        AgentStreamEvent streamEvent, IRecordedTurnCommitter committer, bool persistProgress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(turn);
        ArgumentNullException.ThrowIfNull(streamEvent);
        ArgumentNullException.ThrowIfNull(committer);
        if (turn.Completed) return;

        try
        {
            switch (streamEvent)
            {
                case AssistantTextDeltaEvent text:
                    await ApplyDeltaAsync(session, turn, text.Delta, thinking: false, cancellationToken);
                    break;
                case AssistantThinkingDeltaEvent thinking:
                    await ApplyDeltaAsync(session, turn, thinking.Delta, thinking: true, cancellationToken);
                    break;
                case ToolCallStartedEvent started:
                    await StartToolAsync(session, turn, started, persistProgress, cancellationToken);
                    break;
                case ToolCallCompletedEvent completed:
                    await CompleteToolAsync(session, turn, completed, persistProgress, cancellationToken);
                    break;
                case UsageReportedEvent usage:
                    turn.Usage.Observe(usage.Usage);
                    session.ReplaceTurn(GetTurn(session, turn) with { Usage = turn.Usage.Build() });
                    session.RaiseTranscriptChanged(false);
                    break;
                case TurnInputBoundaryEvent boundary:
                    await CommitBoundaryAsync(session, turn, boundary, cancellationToken);
                    break;
                case RunNoticeEvent notice when !string.IsNullOrWhiteSpace(notice.Text):
                    var noticeMessage = await EnsureAssistantAsync(session, turn, cancellationToken);
                    if (session.ApplyAssistantNotice(noticeMessage, notice.Text)) session.RaiseTranscriptChanged(false);
                    break;
                case RunStatusEvent status:
                    session.ActivityText = MapRunStatusText(status.Status);
                    session.RaiseTranscriptChanged(false);
                    break;
                case RunCompletedEvent completed:
                    await CompleteTurnAsync(session, turn, completed, committer, cancellationToken);
                    break;
            }
        }
        catch (OperationCanceledException exception)
        {
            turn.InputSession?.Cancel(exception.CancellationToken);
            throw;
        }
        catch (Exception exception)
        {
            // The producer may be waiting behind this very event. Wake it before iterator disposal.
            turn.InputSession?.Fail(exception);
            throw;
        }
    }

    private async Task ApplyDeltaAsync(ConversationRuntimeState session, AgentTurnState turn,
        string delta, bool thinking, CancellationToken cancellationToken)
    {
        if (delta.Length == 0 || (turn.CurrentAssistantMessageId is null && string.IsNullOrWhiteSpace(delta))) return;
        var messageId = await EnsureAssistantAsync(session, turn, cancellationToken);
        var changed = thinking
            ? session.ApplyAssistantThinkingDelta(messageId, delta)
            : session.ApplyAssistantDelta(messageId, delta);
        if (!changed) return;
        var first = !turn.HasVisibleDelta;
        turn.HasVisibleDelta = true;
        session.RaiseTranscriptChanged(first);
    }

    private async Task<Guid> EnsureAssistantAsync(ConversationRuntimeState session, AgentTurnState turn,
        CancellationToken cancellationToken)
    {
        if (turn.CurrentAssistantMessageId is { } current) return current;
        var sequence = await _turns.ReserveMessageSequenceAsync(session.ConversationId, cancellationToken);
        var messageId = Guid.NewGuid();
        var now = _timeProvider.GetUtcNow();
        session.ReplaceMessage(new MessageRecord(messageId, session.ConversationId, turn.TurnId, sequence,
            MessageRole.Assistant, string.Empty, MessageStatus.Streaming, now, now,
            AgentName: turn.AgentName, AgentRole: turn.AgentRole));
        turn.CurrentAssistantMessageId = messageId;
        return messageId;
    }

    private async Task StartToolAsync(ConversationRuntimeState session, AgentTurnState turn,
        ToolCallStartedEvent started, bool persistProgress, CancellationToken cancellationToken)
    {
        if (turn.ToolRunsByCallId.ContainsKey(started.ToolCallId)) return;
        var messageId = await EnsureAssistantAsync(session, turn, cancellationToken);
        var now = _timeProvider.GetUtcNow();
        var tool = new ToolExecutionRecord(Guid.NewGuid(), session.ConversationId, started.ToolName,
            string.IsNullOrWhiteSpace(started.ArgumentsJson) ? "{}" : started.ArgumentsJson,
            ToolExecutionStatus.Running, null, started.ToolCallId, null, now, now,
            MessageId: messageId, SourceKind: started.SourceKind, SourceId: started.SourceId,
            DisplayName: started.DisplayName);
        turn.ToolRunsByCallId.Add(started.ToolCallId, tool);
        session.ApplyStreamedToolRun(tool);
        if (persistProgress) await SaveProgressAsync(session, turn, cancellationToken);
        session.RaiseTranscriptChanged(false);
    }

    private async Task CompleteToolAsync(ConversationRuntimeState session, AgentTurnState turn,
        ToolCallCompletedEvent completed, bool persistProgress, CancellationToken cancellationToken)
    {
        if (!turn.ToolRunsByCallId.TryGetValue(completed.ToolCallId, out var started)) return;
        if (started.MessageId != turn.CurrentAssistantMessageId)
            throw new InvalidDataException("A tool result cannot change a sealed assistant fragment.");
        var now = _timeProvider.GetUtcNow();
        var tool = started with
        {
            Status = MapToolStatus(completed.Status),
            ResultSummary = completed.ResultSummary ?? started.ResultSummary,
            ResultContent = completed.ResultContent is null ? started.ResultContent : TranscriptToolResultLimiter.LimitStored(completed.ResultContent),
            DurationMs = (now - started.CreatedAtUtc).TotalMilliseconds,
            UpdatedAtUtc = now,
            HookOutcome = completed.HookOutcome ?? started.HookOutcome
        };
        turn.ToolRunsByCallId[completed.ToolCallId] = tool;
        session.ApplyStreamedToolRun(tool);
        if (persistProgress) await SaveProgressAsync(session, turn, cancellationToken);
        session.RaiseTranscriptChanged(false);
    }

    private Task SaveProgressAsync(ConversationRuntimeState session, AgentTurnState turn, CancellationToken cancellationToken)
    {
        var message = GetCurrentMessage(session, turn)
            ?? throw new InvalidOperationException("Tool progress requires an actual assistant fragment.");
        return _turns.CommitProgressAsync(new ConversationTurnCommit(GetTurn(session, turn), [message],
            GetCurrentTools(turn)), cancellationToken);
    }

    private async Task CommitBoundaryAsync(ConversationRuntimeState session, AgentTurnState turn,
        TurnInputBoundaryEvent boundary, CancellationToken cancellationToken)
    {
        var inputSession = turn.InputSession;
        if (inputSession is null || session.IsDetached || turn.Record.Origin != DirectTurnOrigin.Interactive ||
            turn.Record.ExecutionMode != AgentExecutionMode.Direct || boundary.Batch.TurnId != turn.TurnId ||
            boundary.Batch.ConversationId != session.ConversationId)
            throw new InvalidDataException("This run cannot consume an input boundary.");

        var consumed = await _inputs.ReadConsumptionAsync(boundary.Batch, cancellationToken);
        if (consumed is not null && consumed.Messages.All(user => session.Messages.Any(message => message.Id == user.Id)))
        {
            inputSession.Confirm(consumed);
            return;
        }

        var content = CaptureBoundary(session, turn, boundary.Usage);
        consumed ??= await ConsumeBoundaryAsync(new ConversationInputBoundaryCommit(boundary.Batch, content), cancellationToken);
        foreach (var message in content.Messages) session.ReplaceMessage(message);
        foreach (var message in consumed.Messages) session.ReplaceMessage(message);
        session.ReplaceTurn(consumed.Turn);
        turn.CurrentAssistantMessageId = null;
        inputSession.Confirm(consumed);
        try
        {
            session.RaiseTranscriptChanged(true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "Failed to publish committed input boundary {ClaimId}.", boundary.Batch.ClaimId);
        }
    }

    private ConversationTurnCommit CaptureBoundary(ConversationRuntimeState session, AgentTurnState turn, TurnUsage? usage)
    {
        var tools = GetCurrentTools(turn);
        if (tools.Any(tool => tool.Status is ToolExecutionStatus.Running or ToolExecutionStatus.AwaitingApproval))
            throw new InvalidDataException("An input boundary cannot split an unfinished tool unit.");
        var message = GetCurrentMessage(session, turn);
        var messages = message is null ? Array.Empty<MessageRecord>() :
            [message with { Status = MessageStatus.Sealed, UpdatedAtUtc = _timeProvider.GetUtcNow() }];
        var currentTurn = GetTurn(session, turn);
        return new ConversationTurnCommit(currentTurn with { Usage = usage ?? currentTurn.Usage }, messages, tools);
    }

    private async Task<ConversationInputConsumption> ConsumeBoundaryAsync(
        ConversationInputBoundaryCommit commit, CancellationToken cancellationToken)
    {
        try
        {
            return await _inputs.CommitBoundaryAsync(commit, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A commit response can be lost after SQLite has committed. The claim mappings are the receipt.
            if (await _inputs.ReadConsumptionAsync(commit.Batch, cancellationToken) is { } committed) return committed;
            throw;
        }
    }

    private async Task CompleteTurnAsync(ConversationRuntimeState session, AgentTurnState turn,
        RunCompletedEvent completed, IRecordedTurnCommitter committer, CancellationToken cancellationToken)
    {
        if (turn.CurrentAssistantMessageId is null && !string.IsNullOrWhiteSpace(completed.FinalText))
            await EnsureAssistantAsync(session, turn, cancellationToken);
        var kind = completed.Status switch
        {
            RunCompletionStatus.Succeeded => TurnFinalizationKind.Succeeded,
            RunCompletionStatus.Truncated => TurnFinalizationKind.Truncated,
            RunCompletionStatus.Blocked => TurnFinalizationKind.Blocked,
            _ => TurnFinalizationKind.Failed
        };
        var error = kind switch
        {
            TurnFinalizationKind.Succeeded => null,
            TurnFinalizationKind.Truncated => completed.ErrorMessage,
            TurnFinalizationKind.Blocked => completed.ErrorMessage ?? "The agent run was blocked.",
            _ => completed.ErrorMessage ?? "The agent run failed."
        };
        await FinalizeTurnAsync(session, turn, kind, completed.FinalText, error, committer);
    }

    private async Task FinalizeTurnAsync(ConversationRuntimeState session, AgentTurnState turn,
        TurnFinalizationKind kind, string? finalText, string? errorMessage, IRecordedTurnCommitter committer)
    {
        if (turn.Completed) return;
        if (turn.CurrentAssistantMessageId is { } messageId) session.CompleteAssistantStream(messageId);
        var currentTurn = GetTurn(session, turn);
        turn.PendingFinalization ??= new RecordedTurnFinalizationRequest(currentTurn, GetCurrentMessage(session, turn),
            GetCurrentTools(turn), kind, finalText, errorMessage, turn.Usage.Build() ?? currentTurn.Usage);
        var finalization = ConversationTurnFinalizationBuilder.Build(turn.PendingFinalization, _timeProvider.GetUtcNow());
        bool written;
        try
        {
            written = await committer.TryCommitAsync(new RecordedTurnCommit(finalization,
                turn.PendingFinalization.Kind, turn.PendingFinalization.FinalText, turn.PendingFinalization.ErrorMessage));
        }
        catch (OperationCanceledException exception)
        {
            ApplyUnpersistedFailure(session, turn, exception);
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed to persist terminal state for turn {TurnId}.", turn.TurnId);
            ApplyUnpersistedFailure(session, turn, exception);
            throw;
        }

        if (!written)
        {
            await ReloadCommittedTurnAsync(session, turn);
            return;
        }
        ApplyFinalization(session, turn, finalization, persisted: true);
    }

    private async Task ReloadCommittedTurnAsync(ConversationRuntimeState session, AgentTurnState turn)
    {
        var persistedTurn = (await _turns.ListTurnsAsync(session.ConversationId)).SingleOrDefault(item => item.Id == turn.TurnId);
        if (persistedTurn is null || persistedTurn.Status == ConversationTurnStatus.Running)
            throw new InvalidDataException("The finalization did not commit and no terminal turn could be reloaded.");
        var messages = (await _conversations.ListMessagesAsync(session.ConversationId)).Where(message => message.TurnId == turn.TurnId).ToArray();
        var messageIds = messages.Select(message => message.Id).ToHashSet();
        var tools = (await _conversations.ListToolExecutionsAsync(session.ConversationId))
            .Where(tool => tool.MessageId is { } id && messageIds.Contains(id)).ToArray();
        session.ReplaceTurnContent(new ConversationTurnCommit(persistedTurn, messages, tools));
        turn.ToolRunsByCallId.Clear();
        foreach (var tool in tools) turn.ToolRunsByCallId[tool.CorrelationId ?? tool.Id.ToString("D")] = tool;
        turn.CurrentAssistantMessageId = null;
        turn.Completed = true;
        session.RaiseTranscriptChanged(true);
    }

    private void ApplyUnpersistedFailure(ConversationRuntimeState session, AgentTurnState turn, Exception exception)
    {
        var pending = turn.PendingFinalization ?? throw new InvalidOperationException("The turn has no pending finalization.");
        turn.PendingFinalization = pending with
        {
            Kind = pending.Kind == TurnFinalizationKind.Succeeded ? TurnFinalizationKind.Failed : pending.Kind,
            FinalText = null,
            ErrorMessage = pending.Kind == TurnFinalizationKind.Succeeded
                ? $"Failed to persist terminal state: {exception.Message}"
                : pending.ErrorMessage ?? exception.Message
        };
        ApplyFinalization(session, turn,
            ConversationTurnFinalizationBuilder.Build(turn.PendingFinalization, _timeProvider.GetUtcNow()), persisted: false);
    }

    private static void ApplyFinalization(ConversationRuntimeState session, AgentTurnState turn,
        ConversationTurnCommit finalization, bool persisted)
    {
        session.ReplaceTurn(finalization.Turn);
        foreach (var message in finalization.Messages) session.ReplaceMessage(message);
        foreach (var tool in finalization.ToolExecutions)
        {
            turn.ToolRunsByCallId[tool.CorrelationId ?? tool.Id.ToString("D")] = tool;
            session.UpsertToolRun(tool);
        }
        if (persisted)
        {
            turn.Completed = true;
            turn.CurrentAssistantMessageId = null;
        }
        session.RaiseTranscriptChanged(true);
    }

    private static ConversationTurnRecord GetTurn(ConversationRuntimeState session, AgentTurnState turn)
        => session.Turns.Single(item => item.Id == turn.TurnId);

    private static MessageRecord? GetCurrentMessage(ConversationRuntimeState session, AgentTurnState turn)
        => turn.CurrentAssistantMessageId is { } id ? session.Messages.Single(message => message.Id == id) : null;

    private static IReadOnlyList<ToolExecutionRecord> GetCurrentTools(AgentTurnState turn)
        => turn.CurrentAssistantMessageId is { } id
            ? turn.ToolRunsByCallId.Values.Where(tool => tool.MessageId == id).ToArray()
            : [];

    private static ToolExecutionStatus MapToolStatus(ToolCallStatus status) => status switch
    {
        ToolCallStatus.Completed => ToolExecutionStatus.Completed,
        ToolCallStatus.Failed => ToolExecutionStatus.Failed,
        ToolCallStatus.Canceled => ToolExecutionStatus.Cancelled,
        ToolCallStatus.Blocked => ToolExecutionStatus.Blocked,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unsupported tool call status.")
    };

    private static string MapRunStatusText(AgentRunStatus status) => status switch
    {
        AgentRunStatus.Initializing => "正在初始化...",
        AgentRunStatus.Requesting => "正在请求...",
        AgentRunStatus.Thinking => "正在思考...",
        AgentRunStatus.Running => "正在执行...",
        _ => "准备中..."
    };
}
