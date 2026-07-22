using Microsoft.Extensions.Logging;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Core.Runtime.Agent;
using SelfClaw.Infrastructure.Tools.Transcript;
using SelfClaw.Desktop.Services.Runtime;

namespace SelfClaw.Desktop.ViewModels;

public sealed partial class MainWindowViewModel
{
    /// <summary>
    /// Translates the runtime's <see cref="AgentStreamEvent"/> stream into transcript state
    /// (<see cref="MessageRecord"/> / <see cref="ToolExecutionRecord"/>) for the Vue renderer (plan.md 阶段 6).
    /// <para>
    /// Mapping (T6.1–T6.4):
    /// </para>
    /// <list type="bullet">
    ///   <item>text / thinking deltas → streamed into the single assistant message of the turn, with
    ///         thinking wrapped in the segmenter's internal markers so the Vue segments contract is preserved;</item>
    ///   <item>tool started / completed → a <see cref="ToolExecutionRecord"/> anchored inline in the assistant
    ///         message via <see cref="CaptureToolRunAnchor"/>;</item>
    ///   <item>usage / completion → the assistant message is finalized with token counts, duration and the
    ///         success / failure / cancellation outcome.</item>
    /// </list>
    /// </summary>
    private async Task HandleAgentStreamEventAsync(
        ConversationRuntimeState runtimeState,
        AgentTurnState turnState,
        AgentStreamEvent streamEvent,
        CancellationToken cancellationToken)
    {
        switch (streamEvent)
        {
            case RunStartedEvent:
                EnsureAssistantMessage(runtimeState, turnState);
                break;

            case AssistantTextDeltaEvent textDelta:
                EnsureAssistantMessage(runtimeState, turnState);
                ApplyAssistantDelta(runtimeState, turnState.AssistantMessageId, textDelta.Delta);
                break;

            case AssistantThinkingDeltaEvent thinkingDelta:
                EnsureAssistantMessage(runtimeState, turnState);
                ApplyAssistantDelta(
                    runtimeState,
                    turnState.AssistantMessageId,
                    AssistantMessageSegmenter.WrapThinking(thinkingDelta.Delta));
                break;

            case ToolCallStartedEvent toolStarted:
                EnsureAssistantMessage(runtimeState, turnState);
                await StartToolRunAsync(runtimeState, turnState, toolStarted, cancellationToken);
                break;

            case ToolCallCompletedEvent toolCompleted:
                await CompleteToolRunAsync(runtimeState, turnState, toolCompleted, cancellationToken);
                break;

            case UsageReportedEvent usage:
                turnState.InputTokens = usage.InputTokens ?? turnState.InputTokens;
                turnState.OutputTokens = usage.OutputTokens ?? turnState.OutputTokens;
                break;

            case RunStatusEvent runStatus:
                EnsureAssistantMessage(runtimeState, turnState);
                runtimeState.ActivityText = MapRunStatusText(runStatus.Status);
                PublishRuntimeState(runtimeState, true);
                break;

            case RunCompletedEvent completed:
                await CompleteAssistantTurnAsync(runtimeState, turnState, completed);
                break;

            // RawOutputEvent / PermissionRequestedEvent carry no transcript state in v1.
            default:
                break;
        }
    }

    /// <summary>
    /// Creates the streaming assistant message the turn writes into, once, on the first event that needs it.
    /// Tracked in <see cref="ConversationRuntimeState.ActiveMessageIds"/> so an exception or cancellation
    /// mid-turn finalizes it through the same terminal-state path.
    /// </summary>
    private void EnsureAssistantMessage(ConversationRuntimeState runtimeState, AgentTurnState turnState)
    {
        if (turnState.MessageCreated)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var message = new MessageRecord(
            turnState.AssistantMessageId,
            runtimeState.ConversationId,
            MessageRole.Assistant,
            string.Empty,
            MessageStatus.Streaming,
            now,
            now,
            turnState.AgentId,
            turnState.AgentName,
            turnState.AgentRole);

        runtimeState.ActiveMessageIds.Add(turnState.AssistantMessageId);
        ReplaceMessage(runtimeState, message);
        turnState.MessageCreated = true;
        PublishRuntimeState(runtimeState, true);
    }

    private async Task StartToolRunAsync(
        ConversationRuntimeState runtimeState,
        AgentTurnState turnState,
        ToolCallStartedEvent toolStarted,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var record = new ToolExecutionRecord(
            Id: Guid.NewGuid(),
            ConversationId: runtimeState.ConversationId,
            ToolName: toolStarted.ToolName,
            ArgumentsJson: string.IsNullOrWhiteSpace(toolStarted.ArgumentsJson) ? "{}" : toolStarted.ArgumentsJson,
            Status: ToolExecutionStatus.Running,
            ResultSummary: null,
            CorrelationId: toolStarted.ToolCallId,
            DurationMs: null,
            CreatedAtUtc: now,
            UpdatedAtUtc: now,
            MessageId: turnState.AssistantMessageId,
            AfterSegmentIndex: null);

        var anchored = CaptureToolRunAnchor(runtimeState, record);
        turnState.ToolRunsByCallId[toolStarted.ToolCallId] = anchored;
        UpsertToolRun(runtimeState, anchored);
        await _conversationRepository.UpsertToolExecutionAsync(anchored, cancellationToken);
        PublishRuntimeState(runtimeState, true);
    }

    private async Task CompleteToolRunAsync(
        ConversationRuntimeState runtimeState,
        AgentTurnState turnState,
        ToolCallCompletedEvent toolCompleted,
        CancellationToken cancellationToken)
    {
        if (!turnState.ToolRunsByCallId.TryGetValue(toolCompleted.ToolCallId, out var startedRecord))
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

        var anchored = CaptureToolRunAnchor(runtimeState, updated);
        turnState.ToolRunsByCallId[toolCompleted.ToolCallId] = anchored;
        UpsertToolRun(runtimeState, anchored);
        await _conversationRepository.UpsertToolExecutionAsync(anchored, cancellationToken);
        PublishRuntimeState(runtimeState, true);
    }

    private async Task CompleteAssistantTurnAsync(
        ConversationRuntimeState runtimeState,
        AgentTurnState turnState,
        RunCompletedEvent completed)
    {
        EnsureAssistantMessage(runtimeState, turnState);
        var existing = runtimeState.Messages.FirstOrDefault(item => item.Id == turnState.AssistantMessageId);
        if (existing is null)
        {
            return;
        }

        var kind = completed.Status == RunCompletionStatus.Succeeded
            ? TurnFinalizationKind.Succeeded
            : TurnFinalizationKind.Failed;
        var errorMessage = kind == TurnFinalizationKind.Succeeded
            ? null
            : completed.ErrorMessage
                ?? "The agent run failed.";

        await FinalizeTurnAsync(
            runtimeState,
            turnState,
            existing,
            kind,
            completed.FinalText,
            errorMessage);
    }

    private Task FinalizeInterruptedTurnAsync(
        ConversationRuntimeState runtimeState,
        AgentTurnState turnState,
        TurnFinalizationKind kind,
        string errorMessage)
    {
        EnsureAssistantMessage(runtimeState, turnState);
        var existing = runtimeState.Messages.First(item => item.Id == turnState.AssistantMessageId);
        return FinalizeTurnAsync(
            runtimeState,
            turnState,
            existing,
            kind,
            finalText: null,
            errorMessage);
    }

    private async Task FinalizeTurnAsync(
        ConversationRuntimeState runtimeState,
        AgentTurnState turnState,
        MessageRecord existing,
        TurnFinalizationKind kind,
        string? finalText,
        string? errorMessage)
    {
        if (turnState.Completed)
        {
            return;
        }

        turnState.PendingFinalization ??= new DesktopTurnFinalizationRequest(
                existing,
                turnState.ToolRunsByCallId.Values.ToArray(),
                kind,
                finalText,
                errorMessage,
                turnState.InputTokens,
                turnState.OutputTokens,
                turnState.StartedAtUtc);
        TurnFinalization? finalization;
        try
        {
            finalization = await _turnFinalizer.FinalizeAsync(turnState.PendingFinalization);
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "Failed to persist terminal state for turn {TurnId}.",
                turnState.AssistantMessageId);
            return;
        }
        if (finalization is null)
        {
            turnState.Completed = true;
            runtimeState.ActiveMessageIds.Remove(turnState.AssistantMessageId);
            return;
        }

        ReplaceMessage(runtimeState, finalization.AssistantMessage);
        foreach (var toolExecution in finalization.ToolExecutions)
        {
            turnState.ToolRunsByCallId[toolExecution.CorrelationId ?? toolExecution.Id.ToString("D")] = toolExecution;
            UpsertToolRun(runtimeState, toolExecution);
        }

        turnState.Completed = true;
        runtimeState.ActiveMessageIds.Remove(turnState.AssistantMessageId);
        PublishRuntimeStateNow(runtimeState, true);
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
            AgentRunStatus.Requesting => "正在请求模型...",
            AgentRunStatus.Thinking => "正在思考...",
            AgentRunStatus.Running => "正在执行...",
            _ => "准备中..."
        };

    /// <summary>Per-turn translation state shared across the events of a single <c>StreamTurnAsync</c> call.</summary>
    private sealed class AgentTurnState
    {
        public AgentTurnState(AgentRuntimeDefinition agent)
        {
            AssistantMessageId = Guid.NewGuid();
            AgentName = agent.Name;
            AgentRole = "Agent";
        }

        public Guid AssistantMessageId { get; }

        public Guid? AgentId => null;

        public string AgentName { get; }

        public string AgentRole { get; }

        public DateTimeOffset StartedAtUtc { get; } = DateTimeOffset.UtcNow;

        public int? InputTokens { get; set; }

        public int? OutputTokens { get; set; }

        public bool MessageCreated { get; set; }

        public bool Completed { get; set; }

        public DesktopTurnFinalizationRequest? PendingFinalization { get; set; }

        public Dictionary<string, ToolExecutionRecord> ToolRunsByCallId { get; } = new(StringComparer.Ordinal);
    }
}
