using Microsoft.Extensions.Logging;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Core.Runtime.Agent;
using SelfClaw.Desktop.Services.AgentActivity;
using SelfClaw.Desktop.Services.ProgrammingAssistant;
using SelfClaw.Desktop.Services.Runtime.Abstractions;
using SelfClaw.Infrastructure.Tools.Transcript;

namespace SelfClaw.Desktop.Services.Runtime;

/// <summary>
/// Executes one desktop turn from admission and conversation persistence through runtime streaming and terminal
/// projection. Direct and CLI turns share this workflow because both produce the same agent event stream.
/// </summary>
internal sealed class ConversationTurnEngine : IDisposable
{
    private readonly IConversationRepository _conversationRepository;
    private readonly DesktopTurnFinalizer _turnFinalizer;
    private readonly IAgentChatRuntime _agentChatRuntime;
    private readonly ConversationSessionCoordinator _conversationSessions;
    private readonly AgentActivityCoordinator _agentActivityCoordinator;
    private readonly DesktopToolApprovalHandler _toolApprovalHandler;
    private readonly ProgrammingAssistantSettingsService _programmingAssistantSettings;
    private readonly IConversationCompletionNotifier _completionNotifier;
    private readonly ILogger<ConversationTurnEngine> _logger;
    private readonly SemaphoreSlim _turnAdmissionGate = new(1, 1);
    private int _disposeStarted;

    public ConversationTurnEngine(
        IConversationRepository conversationRepository,
        DesktopTurnFinalizer turnFinalizer,
        IAgentChatRuntime agentChatRuntime,
        ConversationSessionCoordinator conversationSessions,
        AgentActivityCoordinator agentActivityCoordinator,
        DesktopToolApprovalHandler toolApprovalHandler,
        ProgrammingAssistantSettingsService programmingAssistantSettings,
        IConversationCompletionNotifier completionNotifier,
        ILogger<ConversationTurnEngine> logger)
    {
        _conversationRepository = conversationRepository;
        _turnFinalizer = turnFinalizer;
        _agentChatRuntime = agentChatRuntime;
        _conversationSessions = conversationSessions;
        _agentActivityCoordinator = agentActivityCoordinator;
        _toolApprovalHandler = toolApprovalHandler;
        _programmingAssistantSettings = programmingAssistantSettings;
        _completionNotifier = completionNotifier;
        _logger = logger;
    }

    internal async Task<AdmittedConversationTurn?> TryAdmitAsync(DesktopConversationTurnRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        await _turnAdmissionGate.WaitAsync();
        try
        {
            var conversation = CreateConversation(request);
            if (_conversationSessions.IsRunning(conversation.Id))
            {
                return null;
            }

            conversation = await _conversationRepository.UpsertConversationAsync(conversation);
            var runtimeState = await _conversationSessions.StartTurnAsync(conversation);
            return new AdmittedConversationTurn(request, conversation, runtimeState);
        }
        finally
        {
            _turnAdmissionGate.Release();
        }
    }

    internal async Task ExecuteAsync(AdmittedConversationTurn admission)
    {
        ArgumentNullException.ThrowIfNull(admission);

        var runtimeState = admission.RuntimeState;
        AgentTurnState? turnState = null;
        var activityStarted = false;
        try
        {
            await AddUserMessageAsync(admission);
            turnState = new AgentTurnState(admission.Request.Agent);
            var chatRequest = await BuildChatTurnRequestAsync(admission, runtimeState.CancellationTokenSource.Token);
            BeginActivity(admission.Conversation, admission.Request.Agent, turnState);
            activityStarted = true;
            BeginAssistantMessage(runtimeState, turnState);
            await StreamTurnAsync(runtimeState, turnState, chatRequest);

            if (turnState.Completed)
            {
                _completionNotifier.Notify(admission.Conversation, runtimeState.Messages);
            }
        }
        catch (OperationCanceledException) when (runtimeState.CancellationTokenSource.IsCancellationRequested)
        {
            await FinalizeIfStartedAsync(
                runtimeState,
                turnState,
                TurnFinalizationKind.Cancelled,
                AgentActivityOutcome.Cancelled,
                "Generation stopped.");
        }
        catch (Exception exception)
        {
            LogTurnFailure(exception);
            await FinalizeIfStartedAsync(
                runtimeState,
                turnState,
                TurnFinalizationKind.Failed,
                AgentActivityOutcome.Failed,
                exception.Message);
        }
        finally
        {
            CompleteExecution(runtimeState, turnState, activityStarted);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
        {
            return;
        }

        _turnAdmissionGate.Dispose();
    }

    private static ConversationRecord CreateConversation(DesktopConversationTurnRequest request)
    {
        var now = DateTimeOffset.UtcNow;
        var conversation = request.Conversation ?? new ConversationRecord(
            Guid.NewGuid(),
            "New chat",
            request.WorkspaceRoot?.Id,
            ConversationMode.Programming,
            request.ToolPermissionMode,
            request.Agent.Id,
            now,
            now);
        return conversation with
        {
            Title = conversation.Title == "New chat"
                ? CreateConversationTitle(request.Prompt)
                : conversation.Title,
            WorkspaceRootId = request.WorkspaceRoot?.Id,
            Mode = ConversationMode.Programming,
            ToolPermissionMode = request.ToolPermissionMode,
            UpdatedAtUtc = now
        };
    }

    private static string CreateConversationTitle(string text)
    {
        var normalized = text.ReplaceLineEndings(" ").Trim();
        return normalized.Length > 48 ? normalized[..48] + "..." : normalized;
    }

    private void LogTurnFailure(Exception exception)
    {
        if (exception is OperationCanceledException)
        {
            _logger.LogError(exception, "The chat runtime canceled without a user cancellation request.");
            return;
        }

        _logger.LogError(exception, "Chat turn failed.");
    }

    private void CompleteExecution(
        ConversationRuntimeState runtimeState,
        AgentTurnState? turnState,
        bool activityStarted)
    {
        _conversationSessions.CompleteTurn(runtimeState);
        if (activityStarted && turnState is not null && !turnState.Completed)
        {
            _agentActivityCoordinator.CompleteInterrupted(
                turnState.AssistantMessageId,
                AgentActivityOutcome.Failed,
                "Agent stream ended before the turn reached a terminal state.");
        }
    }

    private async Task AddUserMessageAsync(AdmittedConversationTurn admission)
    {
        var request = admission.Request;
        var now = DateTimeOffset.UtcNow;
        var userMessage = new MessageRecord(
            Guid.NewGuid(),
            admission.Conversation.Id,
            MessageRole.User,
            request.Prompt,
            MessageStatus.Completed,
            now,
            now);
        admission.RuntimeState.ReplaceMessage(userMessage);
        await _conversationRepository.UpsertMessageAsync(
            userMessage,
            admission.RuntimeState.CancellationTokenSource.Token);
        admission.RuntimeState.RaiseTranscriptChanged(false);
    }

    private void BeginActivity(
        ConversationRecord conversation,
        AgentRuntimeDefinition agent,
        AgentTurnState turn)
        => _agentActivityCoordinator.BeginTurn(new AgentActivityContext(
            turn.AssistantMessageId,
            conversation.Id,
            conversation.Title,
            agent.Id,
            agent.Name,
            agent.Mode,
            turn.StartedAtUtc));

    private async Task StreamTurnAsync(
        ConversationRuntimeState runtimeState,
        AgentTurnState turnState,
        ChatTurnRequest chatRequest)
    {
        var cancellationToken = runtimeState.CancellationTokenSource.Token;
        await foreach (var update in _agentChatRuntime.StreamTurnAsync(chatRequest, cancellationToken))
        {
            await ApplyEventAsync(runtimeState, turnState, update, cancellationToken);
            _agentActivityCoordinator.ApplyEvent(turnState.AssistantMessageId, update);
        }
    }

    private async Task FinalizeInterruptedTurnAsync(
        ConversationRuntimeState runtimeState,
        AgentTurnState turnState,
        TurnFinalizationKind finalizationKind,
        AgentActivityOutcome activityOutcome,
        string message)
    {
        await FinalizeInterruptedAsync(runtimeState, turnState, finalizationKind, message);
        _agentActivityCoordinator.CompleteInterrupted(turnState.AssistantMessageId, activityOutcome, message);
    }

    private async Task FinalizeIfStartedAsync(
        ConversationRuntimeState runtimeState,
        AgentTurnState? turnState,
        TurnFinalizationKind finalizationKind,
        AgentActivityOutcome activityOutcome,
        string message)
    {
        if (turnState is null)
        {
            return;
        }

        await FinalizeInterruptedTurnAsync(
            runtimeState,
            turnState,
            finalizationKind,
            activityOutcome,
            message);
    }

    private async Task<ChatTurnRequest> BuildChatTurnRequestAsync(
        AdmittedConversationTurn admission,
        CancellationToken cancellationToken)
    {
        var request = admission.Request;
        var messages = admission.RuntimeState.Messages.ToArray();
        if (request.Agent.Mode == AgentExecutionMode.Cli)
        {
            var cliSelection = await _programmingAssistantSettings.GetSelectedInvocationAsync(cancellationToken);
            return new CliChatTurnRequest(
                admission.Conversation.Id,
                request.WorkspaceRoot,
                request.Agent,
                messages,
                cliSelection?.Kind,
                cliSelection?.Model,
                cliSelection?.ReasoningEffort);
        }

        return new DirectChatTurnRequest(
            admission.Conversation.Id,
            request.WorkspaceRoot,
            request.Agent,
            messages,
            request.ModelProfileId,
            request.ToolPermissionMode,
            _toolApprovalHandler);
    }

    /// <summary>
    /// Surfaces the streaming assistant placeholder before the first stream event. CLI process startup can take
    /// seconds before <see cref="RunStartedEvent"/>, and the transcript would otherwise show only the user message.
    /// </summary>
    private void BeginAssistantMessage(ConversationRuntimeState session, AgentTurnState turn)
        => EnsureAssistantMessage(session, turn);

    /// <summary>Applies one stream event to the turn's transcript state, persisting tool runs as they arrive.</summary>
    private async Task ApplyEventAsync(
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
    private Task FinalizeInterruptedAsync(
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
            AfterSegmentIndex: null,
            SourceKind: toolStarted.SourceKind,
            SourceId: toolStarted.SourceId,
            DisplayName: toolStarted.DisplayName);

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
                turn.AssistantMessageId);
            ApplyUnpersistedTerminalFailure(session, turn, exception);
            throw;
        }

        if (finalization is null)
        {
            turn.Completed = true;
            return;
        }

        ApplyFinalization(session, turn, finalization, persisted: true);
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
            _turnFinalizer.CreateFinalization(turn.PendingFinalization),
            persisted: false);
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
