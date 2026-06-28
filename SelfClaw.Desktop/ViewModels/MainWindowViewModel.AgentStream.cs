using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Core.Runtime.Agent;
using SelfClaw.Infrastructure.Tools.Transcript;

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

            case RunCompletedEvent completed:
                await CompleteAssistantTurnAsync(runtimeState, turnState, completed, cancellationToken);
                break;

            // RunStatusEvent / RawOutputEvent / PermissionRequestedEvent carry no transcript state in v1.
            default:
                break;
        }
    }

    /// <summary>
    /// Creates the streaming assistant message the turn writes into, once, on the first event that needs it.
    /// Tracked in <see cref="ConversationRuntimeState.ActiveMessageIds"/> so an exception or cancellation
    /// mid-turn fails it via <c>FailActiveMessagesAsync</c>.
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
        RunCompletedEvent completed,
        CancellationToken cancellationToken)
    {
        // Surface a failure even when no assistant content streamed (e.g. the CLI never started).
        EnsureAssistantMessage(runtimeState, turnState);
        turnState.Completed = true;
        runtimeState.ActiveMessageIds.Remove(turnState.AssistantMessageId);

        var existing = runtimeState.Messages.FirstOrDefault(item => item.Id == turnState.AssistantMessageId);
        if (existing is null)
        {
            return;
        }

        var succeeded = completed.Status == RunCompletionStatus.Succeeded;
        var errorMessage = succeeded
            ? null
            : completed.ErrorMessage
                ?? (completed.Status == RunCompletionStatus.Canceled ? "Generation stopped." : "The agent run failed.");

        var finalMarkdown = completed.FinalText is null
            ? existing.MarkdownContent
            : AssistantMessageSegmenter.MergeFinalMarkdown(completed.FinalText, existing.MarkdownContent);

        var updated = existing with
        {
            MarkdownContent = finalMarkdown,
            Status = succeeded ? MessageStatus.Completed : MessageStatus.Failed,
            InputTokens = turnState.InputTokens,
            OutputTokens = turnState.OutputTokens,
            DurationMs = (DateTimeOffset.UtcNow - turnState.StartedAtUtc).TotalMilliseconds,
            ErrorMessage = errorMessage,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };

        ReplaceMessage(runtimeState, updated);
        await _conversationRepository.UpsertMessageAsync(updated, cancellationToken);
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

        public Dictionary<string, ToolExecutionRecord> ToolRunsByCallId { get; } = new(StringComparer.Ordinal);
    }
}
