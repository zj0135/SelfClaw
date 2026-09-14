using System.Collections.Concurrent;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Desktop.Services.Transcript.Abstractions;

namespace SelfClaw.Desktop.Services.Runtime;

internal sealed class ConversationSessionCoordinator : IDisposable
{
    private readonly IConversationRepository _conversationRepository;
    private readonly ITranscriptChangeSink _transcriptChangeSink;
    private readonly ConcurrentDictionary<Guid, ConversationRuntimeState> _runtimeStates = [];
    private readonly Dictionary<Guid, Task<ConversationTranscriptSnapshot>> _transcriptLoads = [];
    private readonly object _transcriptLoadGate = new();
    private readonly object _selectionGate = new();
    private readonly CancellationTokenSource _loadCancellation = new();
    private ConversationTranscriptSnapshot? _selectedTranscript;
    private readonly SemaphoreSlim _startTurnGate = new(1, 1);
    private Guid? _selectedConversationId;
    private int _selectionVersion;
    private int _disposeStarted;
    private int _stopping;

    public ConversationSessionCoordinator(
        IConversationRepository conversationRepository,
        ITranscriptChangeSink transcriptChangeSink)
    {
        _conversationRepository = conversationRepository;
        _transcriptChangeSink = transcriptChangeSink;
    }

    internal IReadOnlyList<MessageRecord> SelectedMessages
        => CaptureSelectedTranscript().Messages;

    internal IReadOnlyList<ToolExecutionRecord> SelectedToolRuns
        => CaptureSelectedTranscript().ToolRuns;

    internal ConversationTranscriptSnapshot CaptureSelectedTranscript()
    {
        lock (_selectionGate)
            return GetSelectedRuntimeState()?.CaptureSnapshot() ?? _selectedTranscript ?? new([], []);
    }

    internal bool IsSelectedRunning => GetSelectedRuntimeState()?.IsRunning == true;
    internal event Action? SelectedStateChanged;

    internal string? SelectedActivityText => GetSelectedRuntimeState()?.ActivityText;

    internal bool IsSelected(Guid conversationId)
    {
        lock (_selectionGate) return _selectedConversationId == conversationId;
    }

    internal bool IsRunning(Guid conversationId)
        => _runtimeStates.TryGetValue(conversationId, out var state) && state.IsRunning;

    internal IEnumerable<Guid> RunningConversationIds
        => _runtimeStates.Where(pair => pair.Value.IsRunning).Select(pair => pair.Key);

    internal async Task SelectAsync(Guid? conversationId, CancellationToken cancellationToken = default)
    {
        int version;
        lock (_selectionGate)
        {
            version = ++_selectionVersion;
            _selectedConversationId = conversationId;
            ClearSelectedTranscript();
        }
        _transcriptChangeSink.PublishNow(conversationId is not null);

        if (conversationId is not Guid selectedId ||
            (_runtimeStates.TryGetValue(selectedId, out var runtimeState) && !runtimeState.IsDetached))
        {
            return;
        }

        var snapshot = await GetTranscriptSnapshotAsync(selectedId, cancellationToken);

        lock (_selectionGate)
        {
            if (version != _selectionVersion || _selectedConversationId != selectedId || Volatile.Read(ref _stopping) != 0)
                return;
            ReplaceSelectedTranscript(snapshot);
        }
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
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _stopping) != 0, this);

        var snapshot = await GetTranscriptSnapshotAsync(conversation.Id, cancellationToken);
        await _startTurnGate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _stopping) != 0, this);
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
            if (IsSelected(conversation.Id)) SelectedStateChanged?.Invoke();
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

        try
        {
            state.IsRunning = false;
            ForgetTranscriptLoad(state.ConversationId);
            lock (_selectionGate)
            {
                if (IsSelected(state.ConversationId))
                {
                    _selectionVersion++;
                    ReplaceSelectedTranscript(state);
                }
                _runtimeStates.TryRemove(state.ConversationId, out _);
            }
            state.Dispose();
            if (IsSelected(state.ConversationId)) _transcriptChangeSink.PublishNow(true);
            if (IsSelected(state.ConversationId)) SelectedStateChanged?.Invoke();
        }
        finally
        {
            state.MarkCompleted();
        }
    }

    internal void AbandonTurn(ConversationRuntimeState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        state.IsRunning = false;
        if (_runtimeStates.TryRemove(state.ConversationId, out var registered))
        {
            registered.Dispose();
        }
        else
        {
            state.Dispose();
        }
        state.MarkCompleted();
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
            ForgetTranscriptLoad(conversationId);
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

            await state.Completion.WaitAsync(timeout, cancellationToken);
        }

        if (_runtimeStates.TryRemove(conversationId, out var remainingState))
        {
            remainingState.Dispose();
        }

        ForgetTranscriptLoad(conversationId);
    }

    internal async Task StopAsync(CancellationToken cancellationToken)
    {
        Interlocked.Exchange(ref _stopping, 1);
        await _startTurnGate.WaitAsync(cancellationToken);
        ConversationRuntimeState[] running;
        try { running = _runtimeStates.Values.ToArray(); }
        finally { _startTurnGate.Release(); }
        foreach (var state in running)
        {
            try { state.CancellationTokenSource.Cancel(); }
            catch (ObjectDisposedException) { }
        }
        await Task.WhenAll(running.Select(state => state.Completion)).WaitAsync(cancellationToken);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0)
        {
            return;
        }

        Interlocked.Exchange(ref _stopping, 1);
        lock (_selectionGate)
        {
            _selectionVersion++;
            _selectedConversationId = null;
            _selectedTranscript = null;
        }
        foreach (var state in _runtimeStates.Values)
        {
            if (state.IsRunning)
            {
                try { state.CancellationTokenSource.Cancel(); }
                catch (ObjectDisposedException) { }
            }

            if (state.Completion.IsCompleted) state.Dispose();
        }

        _runtimeStates.Clear();
        _loadCancellation.Cancel();
        lock (_transcriptLoadGate) _transcriptLoads.Clear();
        _selectedTranscript = null;
        _loadCancellation.Dispose();
        _startTurnGate.Dispose();
    }

    private ConversationRuntimeState? GetSelectedRuntimeState()
    {
        lock (_selectionGate)
            return _selectedConversationId is Guid id && _runtimeStates.TryGetValue(id, out var state) && !state.IsDetached
                ? state : null;
    }

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
        => _selectedTranscript = CreateSnapshot(state);

    private void ReplaceSelectedTranscript(ConversationTranscriptSnapshot snapshot)
        => _selectedTranscript = snapshot;

    private void ClearSelectedTranscript()
        => _selectedTranscript = null;

    private Task<ConversationTranscriptSnapshot> GetTranscriptSnapshotAsync(
        Guid conversationId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeStarted) != 0, this);
        lock (_selectionGate)
        {
            if (IsSelected(conversationId) && _selectedTranscript is { } selected)
                return Task.FromResult(selected);
        }
        Task<ConversationTranscriptSnapshot> load;
        lock (_transcriptLoadGate)
        {
            if (!_transcriptLoads.TryGetValue(conversationId, out var pending))
            {
                pending = LoadTranscriptAsync(conversationId, _loadCancellation.Token);
                _transcriptLoads.Add(conversationId, pending);
                _ = pending.ContinueWith(completed =>
                {
                    if (completed.IsFaulted) _ = completed.Exception;
                    ForgetTranscriptLoad(conversationId, completed);
                },
                    CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
            }

            load = pending;
        }

        return AwaitTranscriptLoadAsync(conversationId, load, cancellationToken);
    }

    private async Task<ConversationTranscriptSnapshot> AwaitTranscriptLoadAsync(Guid id,
        Task<ConversationTranscriptSnapshot> load, CancellationToken cancellationToken)
    {
        try { return await load.WaitAsync(cancellationToken); }
        finally { if (load.IsCompleted) ForgetTranscriptLoad(id, load); }
    }

    private void ForgetTranscriptLoad(Guid conversationId, Task<ConversationTranscriptSnapshot>? expected = null)
    {
        lock (_transcriptLoadGate)
        {
            if (expected is null || (_transcriptLoads.TryGetValue(conversationId, out var current) && ReferenceEquals(current, expected)))
                _transcriptLoads.Remove(conversationId);
        }
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
        => state.CaptureSnapshot();

    private static ConversationTranscriptSnapshot CreateSnapshot(
        IEnumerable<MessageRecord> messages,
        IEnumerable<ToolExecutionRecord> toolRuns)
    {
        var messageSnapshot = messages.ToArray();
        var toolRunSnapshot = toolRuns.ToArray();

        return new ConversationTranscriptSnapshot(messageSnapshot, toolRunSnapshot);
    }
}
