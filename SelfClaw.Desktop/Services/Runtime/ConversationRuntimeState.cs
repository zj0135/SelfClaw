using SelfClaw.Core.Models;
using SelfClaw.Infrastructure.Tools.Transcript;
using SelfClaw.Infrastructure.Tools.Transcript.Models;

namespace SelfClaw.Desktop.Services.Runtime;

/// <summary>
/// Per-conversation turn state: the transcript projection (messages, tool runs, inline anchors) plus the
/// run lifecycle (cancellation source, running flag, completion signal). Mutation of the transcript happens
/// through the methods here so the reduction rules stay one place and are testable without WPF; the owner
/// subscribes to <see cref="TranscriptChanged"/> to publish snapshots. Reads are surfaced back to the
/// selected conversation only when it is the one on screen.
/// </summary>
internal sealed class ConversationRuntimeState : IDisposable
{
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ConversationRuntimeState(
        ConversationRecord conversation,
        IEnumerable<MessageRecord> messages,
        IEnumerable<ToolExecutionRecord> toolRuns,
        IReadOnlyDictionary<Guid, ToolRunAnchor> toolRunAnchors,
        bool isDetached = false)
    {
        Conversation = conversation;
        IsDetached = isDetached;
        Messages.AddRange(messages);
        ToolRuns.AddRange(toolRuns);
        foreach (var item in toolRunAnchors)
        {
            ToolRunAnchors[item.Key] = item.Value;
        }
    }

    public ConversationRecord Conversation { get; set; }

    public Guid ConversationId => Conversation.Id;

    public bool IsDetached { get; }

    public List<MessageRecord> Messages { get; } = [];

    public List<ToolExecutionRecord> ToolRuns { get; } = [];

    public Dictionary<Guid, ToolRunAnchor> ToolRunAnchors { get; } = [];

    /// <summary>Latest RunStatusEvent text, shown while the streaming message has no content yet.</summary>
    public string? ActivityText { get; set; }

    public CancellationTokenSource CancellationTokenSource { get; } = new();

    public bool IsRunning { get; set; } = true;

    public Task Completion => _completion.Task;

    /// <summary>
    /// Raised after a transcript mutation. <c>true</c> requests an immediate publish (terminal snapshot);
    /// <c>false</c> lets the owner throttle streaming ticks. autoScroll is implied for every turn update.
    /// </summary>
    public event Action<bool>? TranscriptChanged;

    public void RaiseTranscriptChanged(bool immediate) => TranscriptChanged?.Invoke(immediate);

    public void MarkCompleted() => _completion.TrySetResult();

    public void Dispose() => CancellationTokenSource.Dispose();

    public void ReplaceMessage(MessageRecord message)
    {
        var index = Messages.FindIndex(item => item.Id == message.Id);
        if (index >= 0)
        {
            Messages[index] = message;
        }
        else
        {
            Messages.Add(message);
        }
    }

    public void UpsertToolRun(ToolExecutionRecord record)
    {
        var index = ToolRuns.FindIndex(item => item.Id == record.Id);
        if (index >= 0)
        {
            ToolRuns[index] = record;
        }
        else
        {
            ToolRuns.Add(record);
        }
    }

    /// <summary>Appends a streamed delta onto an existing message. Returns whether anything changed.</summary>
    public bool ApplyAssistantDelta(Guid messageId, string deltaMarkdown)
    {
        if (string.IsNullOrWhiteSpace(deltaMarkdown))
        {
            return false;
        }

        var message = Messages.FirstOrDefault(item => item.Id == messageId);
        if (message is null)
        {
            return false;
        }

        ReplaceMessage(message with
        {
            MarkdownContent = message.MarkdownContent + deltaMarkdown,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        });
        return true;
    }

    /// <summary>
    /// Places a tool run inline in its assistant message: reuses a captured anchor when present, otherwise
    /// appends a tool anchor to the assistant markdown and records the resulting segment index so the tool
    /// card renders where the model emitted the call.
    /// </summary>
    public ToolExecutionRecord CaptureToolRunAnchor(ToolExecutionRecord toolRun)
    {
        if (ToolRunAnchors.TryGetValue(toolRun.Id, out var existingAnchor))
        {
            return toolRun with
            {
                MessageId = existingAnchor.MessageId,
                AfterSegmentIndex = existingAnchor.AfterSegmentIndex
            };
        }

        if (toolRun.MessageId is Guid messageId && toolRun.AfterSegmentIndex is int afterSegmentIndex)
        {
            ToolRunAnchors[toolRun.Id] = new ToolRunAnchor(messageId, afterSegmentIndex);
            return toolRun;
        }

        if (toolRun.MessageId is not Guid anchoredMessageId)
        {
            return toolRun;
        }

        var message = Messages.FirstOrDefault(item => item.Id == anchoredMessageId);
        if (message is null)
        {
            return toolRun;
        }

        var anchoredMarkdown = AssistantMessageSegmenter.AppendToolAnchor(message.MarkdownContent, toolRun.Id);
        var anchoredSegments = message.Role == MessageRole.Assistant
            ? AssistantMessageSegmenter.Split(anchoredMarkdown).Segments
            : [];
        var anchorIndex = anchoredSegments
            .Select((item, index) => (item, index))
            .FirstOrDefault(entry =>
                entry.item.Kind == AssistantMessageSegmentKind.ToolAnchor &&
                entry.item.ToolExecutionId == toolRun.Id)
            .index;
        var anchorAfterSegmentIndex = anchorIndex > 0 ? anchorIndex - 1 : -1;
        var anchor = new ToolRunAnchor(anchoredMessageId, anchorAfterSegmentIndex);

        ReplaceMessage(message with
        {
            MarkdownContent = anchoredMarkdown,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        });

        ToolRunAnchors[toolRun.Id] = anchor;
        return toolRun with
        {
            MessageId = anchor.MessageId,
            AfterSegmentIndex = anchor.AfterSegmentIndex
        };
    }
}
