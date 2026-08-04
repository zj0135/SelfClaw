using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Desktop.Services.Transcript.Abstractions;
using SelfClaw.Infrastructure.Tools.Transcript.Models;

namespace SelfClaw.Desktop.Services.Runtime;

internal sealed class ConversationSessionCoordinator : IDisposable
{
    private readonly IConversationRepository _conversationRepository;
    private readonly ITranscriptChangeSink _transcriptChangeSink;
    private readonly Dictionary<Guid, ConversationRuntimeState> _runtimeStates = [];
    private readonly Dictionary<Guid, Task<ConversationTranscriptSnapshot>> _transcriptLoads = [];
    private readonly List<MessageRecord> _selectedMessages = [];
    private readonly List<ToolExecutionRecord> _selectedToolRuns = [];
    private readonly Dictionary<Guid, ToolRunAnchor> _selectedToolRunAnchors = [];
    private readonly SemaphoreSlim _startTurnGate = new(1, 1);
    private Guid? _selectedConversationId;
    private int _selectionVersion;
    private int _disposeStarted;

    public ConversationSessionCoordinator(
        IConversationRepository conversationRepository,
        ITranscriptChangeSink transcriptChangeSink)
    {
        _conversationRepository = conversationRepository;
        _transcriptChangeSink = transcriptChangeSink;
    }

    internal IReadOnlyList<MessageRecord> SelectedMessages
        => GetSelectedRuntimeState()?.Messages ?? _selectedMessages;

    internal IReadOnlyList<ToolExecutionRecord> SelectedToolRuns
        => GetSelectedRuntimeState()?.ToolRuns ?? _selectedToolRuns;

    internal IReadOnlyDictionary<Guid, ToolRunAnchor> SelectedToolRunAnchors
        => GetSelectedRuntimeState()?.ToolRunAnchors ?? _selectedToolRunAnchors;

    internal bool IsSelectedRunning => GetSelectedRuntimeState()?.IsRunning == true;

    internal string? SelectedActivityText => GetSelectedRuntimeState()?.ActivityText;

    internal bool IsSelected(Guid conversationId) => _selectedConversationId == conversationId;

    internal bool IsRunning(Guid conversationId)
        => _runtimeStates.TryGetValue(conversationId, out var state) && state.IsRunning;

    internal async Task SelectAsync(Guid? conversationId, CancellationToken cancellationToken = default)
    {
        var version = ++_selectionVersion;
        _selectedConversationId = conversationId;
        ClearSelectedTranscript();
        _transcriptChangeSink.PublishNow(false);

        if (conversationId is not Guid selectedId || _runtimeStates.ContainsKey(selectedId))
        {
            return;
        }

        var loadTask = LoadTranscriptAsync(selectedId, cancellationToken);
        _transcriptLoads[selectedId] = loadTask;
        var snapshot = await loadTask;

        if (version != _selectionVersion || _selectedConversationId != selectedId)
        {
            return;
        }

        ReplaceSelectedTranscript(snapshot);
        _transcriptChangeSink.PublishNow(false);
    }

    internal async Task<ConversationRuntimeState> StartTurnAsync(
        ConversationRecord conversation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(conversation);

        var snapshot = await GetTranscriptSnapshotAsync(conversation.Id, cancellationToken);
        await _startTurnGate.WaitAsync(cancellationToken);
        try
        {
            if (_runtimeStates.TryGetValue(conversation.Id, out var existing))
            {
                if (existing.IsRunning)
                {
                    throw new InvalidOperationException("This conversation is already running.");
                }

                existing.Dispose();
                _runtimeStates.Remove(conversation.Id);
            }

            var state = new ConversationRuntimeState(
                conversation,
                snapshot.Messages,
                snapshot.ToolRuns,
                snapshot.ToolRunAnchors);
            state.TranscriptChanged += immediate =>
            {
                if (IsSelected(state.ConversationId))
                {
                    PublishSelectedTranscriptChange(immediate);
                }
            };
            _runtimeStates[conversation.Id] = state;
            return state;
        }
        finally
        {
            _startTurnGate.Release();
        }
    }

    internal void CompleteTurn(ConversationRuntimeState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        state.IsRunning = false;
        state.MarkCompleted();
        _transcriptLoads[state.ConversationId] = Task.FromResult(CreateSnapshot(state));
        if (IsSelected(state.ConversationId))
        {
            ReplaceSelectedTranscript(state);
        }

        _runtimeStates.Remove(state.ConversationId);
        state.Dispose();

        if (IsSelected(state.ConversationId))
        {
            _transcriptChangeSink.PublishNow(true);
        }
    }

    internal void StopSelected()
    {
        var state = GetSelectedRuntimeState();
        if (state?.IsRunning != true)
        {
            return;
        }

        try
        {
            state.CancellationTokenSource.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The turn completed between the running check and cancellation.
        }
    }

    internal async Task StopAndRemoveAsync(
        Guid conversationId,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        if (!_runtimeStates.TryGetValue(conversationId, out var state))
        {
            _transcriptLoads.Remove(conversationId);
            return;
        }

        if (state.IsRunning)
        {
            try
            {
                state.CancellationTokenSource.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The turn completed between lookup and cancellation.
            }

            var delayTask = Task.Delay(timeout, cancellationToken);
            var completedTask = await Task.WhenAny(state.Completion, delayTask);
            if (completedTask != state.Completion && state.IsRunning)
            {
                throw new TimeoutException("The conversation is still running and cannot be deleted yet.");
            }
        }

        if (_runtimeStates.Remove(conversationId, out var remainingState))
        {
            remainingState.Dispose();
        }

        _transcriptLoads.Remove(conversationId);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
        {
            return;
        }

        foreach (var state in _runtimeStates.Values)
        {
            if (state.IsRunning)
            {
                state.CancellationTokenSource.Cancel();
            }

            state.Dispose();
        }

        _runtimeStates.Clear();
        _transcriptLoads.Clear();
        _startTurnGate.Dispose();
    }

    private ConversationRuntimeState? GetSelectedRuntimeState()
        => _selectedConversationId is Guid conversationId &&
           _runtimeStates.TryGetValue(conversationId, out var state)
            ? state
            : null;

    private void PublishSelectedTranscriptChange(bool immediate)
    {
        if (immediate)
        {
            _transcriptChangeSink.PublishNow(true);
            return;
        }

        _transcriptChangeSink.RequestStreamingPublish(true);
    }

    private void ReplaceSelectedTranscript(ConversationRuntimeState state)
    {
        ReplaceList(_selectedMessages, state.Messages);
        ReplaceList(_selectedToolRuns, state.ToolRuns);
        _selectedToolRunAnchors.Clear();
        foreach (var item in state.ToolRunAnchors)
        {
            _selectedToolRunAnchors[item.Key] = item.Value;
        }
    }

    private void ReplaceSelectedTranscript(ConversationTranscriptSnapshot snapshot)
    {
        ReplaceList(_selectedMessages, snapshot.Messages);
        ReplaceList(_selectedToolRuns, snapshot.ToolRuns);
        _selectedToolRunAnchors.Clear();
        foreach (var item in snapshot.ToolRunAnchors)
        {
            _selectedToolRunAnchors[item.Key] = item.Value;
        }
    }

    private void ClearSelectedTranscript()
    {
        _selectedMessages.Clear();
        _selectedToolRuns.Clear();
        _selectedToolRunAnchors.Clear();
    }

    private static void ReplaceList<T>(List<T> target, IEnumerable<T> source)
    {
        target.Clear();
        target.AddRange(source);
    }

    private async Task<ConversationTranscriptSnapshot> GetTranscriptSnapshotAsync(
        Guid conversationId,
        CancellationToken cancellationToken)
    {
        if (!_transcriptLoads.TryGetValue(conversationId, out var loadTask))
        {
            loadTask = LoadTranscriptAsync(conversationId, cancellationToken);
            _transcriptLoads[conversationId] = loadTask;
        }

        return await loadTask;
    }

    private async Task<ConversationTranscriptSnapshot> LoadTranscriptAsync(
        Guid conversationId,
        CancellationToken cancellationToken)
    {
        var messagesTask = _conversationRepository.ListMessagesAsync(conversationId, cancellationToken);
        var toolRunsTask = _conversationRepository.ListToolExecutionsAsync(conversationId, cancellationToken);
        await Task.WhenAll(messagesTask, toolRunsTask);
        return CreateSnapshot(await messagesTask, await toolRunsTask);
    }

    private static ConversationTranscriptSnapshot CreateSnapshot(ConversationRuntimeState state)
        => new(
            state.Messages.ToArray(),
            state.ToolRuns.ToArray(),
            new Dictionary<Guid, ToolRunAnchor>(state.ToolRunAnchors));

    private static ConversationTranscriptSnapshot CreateSnapshot(
        IEnumerable<MessageRecord> messages,
        IEnumerable<ToolExecutionRecord> toolRuns)
    {
        var messageSnapshot = messages.ToArray();
        var toolRunSnapshot = toolRuns.ToArray();
        var anchors = new Dictionary<Guid, ToolRunAnchor>();
        foreach (var toolRun in toolRunSnapshot)
        {
            if (toolRun.MessageId is Guid messageId && toolRun.AfterSegmentIndex is int afterSegmentIndex)
            {
                anchors[toolRun.Id] = new ToolRunAnchor(messageId, afterSegmentIndex);
            }
        }

        return new ConversationTranscriptSnapshot(messageSnapshot, toolRunSnapshot, anchors);
    }
}
