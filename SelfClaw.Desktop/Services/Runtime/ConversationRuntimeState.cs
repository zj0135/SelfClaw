using SelfClaw.Core.Models;

namespace SelfClaw.Desktop.Services.Runtime;

/// <summary>Serializes transcript content mutations and materializes immutable snapshots.</summary>
internal sealed class ConversationRuntimeState
{
    private readonly object _gate = new();
    private readonly List<ConversationTurnRecord> _turns = [];
    private readonly List<MessageRecord> _messages = [];
    private readonly List<ToolExecutionRecord> _toolRuns = [];
    private ConversationTranscriptSnapshot? _snapshot;
    private readonly Dictionary<Guid, (StreamingAssistantContent Stream, long MaterializedRevision)> _messageStreams = [];

    public ConversationRuntimeState(
        ConversationRecord conversation,
        IReadOnlyList<ConversationTurnRecord> turns,
        IReadOnlyList<MessageRecord> messages,
        IReadOnlyList<ToolExecutionRecord> toolRuns,
        bool isDetached = false)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        ArgumentNullException.ThrowIfNull(turns);
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(toolRuns);
        Conversation = conversation;
        IsDetached = isDetached;
        var initialTurns = turns.ToArray();
        var initialMessages = messages.OrderBy(message => message.Sequence).ToArray();
        var initialToolRuns = toolRuns.ToArray();
        InitialSnapshot = new ConversationTranscriptSnapshot(initialTurns, initialMessages, initialToolRuns);
        _turns.AddRange(initialTurns);
        _messages.AddRange(initialMessages);
        _toolRuns.AddRange(initialToolRuns);
    }

    public ConversationRecord Conversation { get; set; }

    public Guid ConversationId => Conversation.Id;

    public bool IsDetached { get; }

    /// <summary>
    /// The persisted transcript this turn started from. A detached turn streams provisionally and is never
    /// persisted, so abandoning it restores this snapshot and the provisional content disappears.
    /// </summary>
    public ConversationTranscriptSnapshot InitialSnapshot { get; }

    public IReadOnlyList<ConversationTurnRecord> Turns => CaptureSnapshot().Turns;

    public IReadOnlyList<MessageRecord> Messages => CaptureSnapshot().Messages;

    public IReadOnlyList<ToolExecutionRecord> ToolRuns => CaptureSnapshot().ToolRuns;

    public ConversationTranscriptSnapshot CaptureSnapshot()
    {
        lock (_gate)
        {
            if (_snapshot is not null) return _snapshot;
            MaterializeStreamingMessages();
            return _snapshot = new ConversationTranscriptSnapshot(
                _turns.ToArray(), _messages.OrderBy(message => message.Sequence).ToArray(), _toolRuns.ToArray());
        }
    }

    /// <summary>Latest RunStatusEvent text, shown while the streaming message has no content yet.</summary>
    public string? ActivityText { get; set; }

    /// <summary>
    /// Raised after a transcript mutation. <c>true</c> requests an immediate publish (terminal snapshot);
    /// <c>false</c> lets the owner throttle streaming ticks. autoScroll is implied for every turn update.
    /// </summary>
    public event Action<bool>? TranscriptChanged;

    public void RaiseTranscriptChanged(bool immediate) => TranscriptChanged?.Invoke(immediate);

    public void ReplaceTurn(ConversationTurnRecord turn)
    {
        ArgumentNullException.ThrowIfNull(turn);
        if (turn.ConversationId != ConversationId) throw new ArgumentException("The turn belongs to another conversation.", nameof(turn));
        lock (_gate)
        {
            _snapshot = null;
            var index = _turns.FindIndex(item => item.Id == turn.Id);
            if (index >= 0) _turns[index] = turn;
            else _turns.Add(turn);
        }
    }

    public void ReplaceTurnContent(ConversationTurnCommit commit)
    {
        ArgumentNullException.ThrowIfNull(commit);
        if (commit.Turn.ConversationId != ConversationId) throw new ArgumentException("The turn belongs to another conversation.", nameof(commit));
        lock (_gate)
        {
            var removed = _messages.Where(message => message.TurnId == commit.Turn.Id).Select(message => message.Id).ToHashSet();
            _messages.RemoveAll(message => removed.Contains(message.Id));
            _toolRuns.RemoveAll(tool => tool.MessageId is Guid id && removed.Contains(id));
            foreach (var id in removed) _messageStreams.Remove(id);
            ReplaceTurn(commit.Turn);
            foreach (var message in commit.Messages) ReplaceMessage(message);
            foreach (var tool in commit.ToolExecutions) UpsertToolRun(tool);
        }
    }

    public void ReplaceMessage(MessageRecord message)
    {
        lock (_gate)
        {
            _snapshot = null;
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
    }

    public void UpsertToolRun(ToolExecutionRecord record)
    {
        lock (_gate)
        {
            _snapshot = null;
            var index = _toolRuns.FindIndex(item => item.Id == record.Id);
            if (index >= 0)
            {
                _toolRuns[index] = record;
            }
            else
            {
                _toolRuns.Add(record);
            }
        }
    }

    public void ApplyStreamedToolRun(ToolExecutionRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        lock (_gate)
        {
            CaptureToolRunPlacement(record);
            UpsertToolRun(record);
        }
    }

    /// <summary>
    /// Appends a streamed delta onto an existing message. Returns whether anything changed.
    /// Only truly empty deltas are dropped: newlines and indentation are valid model output
    /// and must stay visible while streaming.
    /// </summary>
    public bool ApplyAssistantDelta(Guid messageId, string deltaMarkdown)
    {
        lock (_gate)
        {
            _snapshot = null;
            if (string.IsNullOrEmpty(deltaMarkdown))
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
    }

    public bool ApplyAssistantThinkingDelta(Guid messageId, string deltaMarkdown)
    {
        lock (_gate)
        {
            _snapshot = null;
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
    }

    /// <summary>
    /// Appends a host-generated turn notice as its own block. Notices are never replayed to the model;
    /// they only exist to explain hook behaviour in the transcript.
    /// </summary>
    public bool ApplyAssistantNotice(Guid messageId, string text)
    {
        lock (_gate)
        {
            _snapshot = null;
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            var message = _messages.FirstOrDefault(item => item.Id == messageId);
            if (message is null)
            {
                return false;
            }

            GetOrCreateMessageStream(message).AppendNotice(text, DateTimeOffset.UtcNow);
            return true;
        }
    }

    /// <summary>
    /// Places a tool run inline in its assistant message by appending a ToolCall block to the
    /// streaming content; the block ordinal is the transcript position of the tool card.
    /// </summary>
    private ToolExecutionRecord CaptureToolRunPlacement(ToolExecutionRecord toolRun)
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
        lock (_gate)
        {
            _snapshot = null;
            if (!_messageStreams.TryGetValue(messageId, out var entry))
            {
                return;
            }

            entry.Stream.CompleteThinking(DateTimeOffset.UtcNow);
            MaterializeMessage(messageId);
        }
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
