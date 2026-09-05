using SelfClaw.Core.Models;

namespace SelfClaw.Desktop.Services.Runtime;

/// <summary>
/// Per-conversation turn state: the transcript projection (messages, tool runs) plus the
/// run lifecycle (cancellation source, running flag, completion signal). Mutation of the transcript happens
/// through the methods here so the reduction rules stay one place and are testable without WPF; the owner
/// subscribes to <see cref="TranscriptChanged"/> to publish snapshots. Reads are surfaced back to the
/// selected conversation only when it is the one on screen.
/// </summary>
internal sealed class ConversationRuntimeState : IDisposable
{
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<MessageRecord> _messages = [];
    private readonly Dictionary<Guid, (StreamingAssistantContent Stream, long MaterializedRevision)> _messageStreams = [];

    public ConversationRuntimeState(
        ConversationRecord conversation,
        IEnumerable<MessageRecord> messages,
        IEnumerable<ToolExecutionRecord> toolRuns,
        bool isDetached = false)
    {
        Conversation = conversation;
        IsDetached = isDetached;
        _messages.AddRange(messages);
        ToolRuns.AddRange(toolRuns);
    }

    public ConversationRecord Conversation { get; set; }

    public Guid ConversationId => Conversation.Id;

    public bool IsDetached { get; }

    public IReadOnlyList<MessageRecord> Messages
    {
        get
        {
            MaterializeStreamingMessages();
            return _messages;
        }
    }

    public List<ToolExecutionRecord> ToolRuns { get; } = [];

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
        var index = _messages.FindIndex(item => item.Id == message.Id);
        if (index >= 0)
        {
            _messages[index] = message;
        }
        else
        {
            _messages.Add(message);
        }

        _messageStreams.Remove(message.Id);
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

        var message = _messages.FirstOrDefault(item => item.Id == messageId);
        if (message is null)
        {
            return false;
        }

        GetOrCreateMessageStream(message).AppendText(deltaMarkdown, DateTimeOffset.UtcNow);
        return true;
    }

    public bool ApplyAssistantThinkingDelta(Guid messageId, string deltaMarkdown)
    {
        if (string.IsNullOrEmpty(deltaMarkdown))
        {
            return false;
        }

        var message = _messages.FirstOrDefault(item => item.Id == messageId);
        if (message is null)
        {
            return false;
        }

        GetOrCreateMessageStream(message).AppendThinking(deltaMarkdown, DateTimeOffset.UtcNow);
        return true;
    }

    /// <summary>
    /// Places a tool run inline in its assistant message by appending a ToolCall block to the
    /// streaming content; the block ordinal is the transcript position of the tool card.
    /// </summary>
    public ToolExecutionRecord CaptureToolRunPlacement(ToolExecutionRecord toolRun)
    {
        if (toolRun.MessageId is not Guid anchoredMessageId)
        {
            return toolRun;
        }

        var message = _messages.FirstOrDefault(item => item.Id == anchoredMessageId);
        if (message is null)
        {
            return toolRun;
        }

        var stream = GetOrCreateMessageStream(message);
        if (stream.BuildSegments().All(segment => segment.ToolRunId != toolRun.Id))
        {
            stream.AppendToolCall(toolRun.Id, DateTimeOffset.UtcNow);
        }

        var index = _messages.FindIndex(item => item.Id == anchoredMessageId);
        _messages[index] = message with
        {
            MarkdownContent = stream.BuildMarkdown(),
            Segments = stream.BuildSegments(),
            UpdatedAtUtc = stream.UpdatedAtUtc
        };
        _messageStreams[anchoredMessageId] = (stream, stream.Revision);

        return toolRun;
    }

    public void CompleteAssistantStream(Guid messageId)
    {
        if (!_messageStreams.TryGetValue(messageId, out var entry))
        {
            return;
        }

        entry.Stream.CompleteThinking(DateTimeOffset.UtcNow);
        MaterializeMessage(messageId);
    }

    private StreamingAssistantContent GetOrCreateMessageStream(MessageRecord message)
    {
        if (_messageStreams.TryGetValue(message.Id, out var existing))
        {
            return existing.Stream;
        }

        var stream = new StreamingAssistantContent();
        stream.Initialize(message.Id, message.Segments, message.UpdatedAtUtc);
        _messageStreams[message.Id] = (stream, stream.Revision);
        return stream;
    }

    private void MaterializeStreamingMessages()
    {
        foreach (var messageId in _messageStreams.Keys.ToArray())
        {
            MaterializeMessage(messageId);
        }
    }

    private void MaterializeMessage(Guid messageId)
    {
        if (!_messageStreams.TryGetValue(messageId, out var entry) ||
            entry.MaterializedRevision == entry.Stream.Revision)
        {
            return;
        }

        var index = _messages.FindIndex(item => item.Id == messageId);
        if (index < 0)
        {
            _messageStreams.Remove(messageId);
            return;
        }

        _messages[index] = _messages[index] with
        {
            MarkdownContent = entry.Stream.BuildMarkdown(),
            Segments = entry.Stream.BuildSegments(),
            UpdatedAtUtc = entry.Stream.UpdatedAtUtc
        };
        _messageStreams[messageId] = (entry.Stream, entry.Stream.Revision);
    }
}
