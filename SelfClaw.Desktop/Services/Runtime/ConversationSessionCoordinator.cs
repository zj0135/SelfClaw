using System.Collections.Concurrent;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Desktop.Services.Transcript.Abstractions;
using SelfClaw.Infrastructure.Tools.Transcript.Models;

namespace SelfClaw.Desktop.Services.Runtime;

internal sealed class ConversationSessionCoordinator : IDisposable
{
    private readonly IConversationRepository _conversationRepository;
    private readonly ITranscriptChangeSink _transcriptChangeSink;
    private readonly ConcurrentDictionary<Guid, ConversationRuntimeState> _runtimeStates = [];
    private readonly ConcurrentDictionary<Guid, Task<ConversationTranscriptSnapshot>> _transcriptLoads = [];
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
        _transcriptChangeSink.PublishNow(conversationId is not null);

        if (conversationId is not Guid selectedId ||
            (_runtimeStates.TryGetValue(selectedId, out var runtimeState) && !runtimeState.IsDetached))
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
        _transcriptChangeSink.PublishNow(true);
    }

    internal async Task<ConversationRuntimeState> StartTurnAsync(
        ConversationRecord conversation,
        CancellationToken cancellationToken = default)
        => await StartTurnCoreAsync(conversation, isDetached: false, cancellationToken);

    internal async Task<ConversationRuntimeState> StartDetachedTurnAsync(
        ConversationRecord conversation,
        CancellationToken cancellationToken = default)
        => await StartTurnCoreAsync(conversation, isDetached: true, cancellationToken);

    private async Task<ConversationRuntimeState> StartTurnCoreAsync(
        ConversationRecord conversation,
        bool isDetached,
        CancellationToken cancellationToken)
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
                _runtimeStates.TryRemove(conversation.Id, out _);
            }

            var state = new ConversationRuntimeState(
                conversation,
                snapshot.Messages,
                snapshot.ToolRuns,
                snapshot.ToolRunAnchors,
                isDetached);
            if (!isDetached)
            {
                state.TranscriptChanged += immediate =>
                {
                    if (IsSelected(state.ConversationId))
                    {
                        PublishSelectedTranscriptChange(immediate);
                    }
                };
            }

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

        _runtimeStates.TryRemove(state.ConversationId, out _);
        state.Dispose();

        if (IsSelected(state.ConversationId))
        {
            _transcriptChangeSink.PublishNow(true);
        }
    }

    internal void AbandonTurn(ConversationRuntimeState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        state.IsRunning = false;
        state.MarkCompleted();
        if (_runtimeStates.TryRemove(state.ConversationId, out var registered))
        {
            registered.Dispose();
        }
        else
        {
            state.Dispose();
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
            _transcriptLoads.TryRemove(conversationId, out _);
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

        if (_runtimeStates.TryRemove(conversationId, out var remainingState))
        {
            remainingState.Dispose();
        }

        _transcriptLoads.TryRemove(conversationId, out _);
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
           _runtimeStates.TryGetValue(conversationId, out var state) &&
           !state.IsDetached
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
