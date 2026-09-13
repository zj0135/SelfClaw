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
    private readonly Dictionary<Guid, TaskCompletionSource<ConversationTranscriptSnapshot>> _transcriptLoads = [];
    private readonly object _transcriptLoadGate = new();
    private readonly CancellationTokenSource _loadCancellation = new();
    private ConversationTranscriptSnapshot? _selectedTranscript;
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
        => GetSelectedRuntimeState()?.Messages ?? _selectedTranscript?.Messages ?? [];

    internal IReadOnlyList<ToolExecutionRecord> SelectedToolRuns
        => GetSelectedRuntimeState()?.ToolRuns ?? _selectedTranscript?.ToolRuns ?? [];

    internal bool IsSelectedRunning => GetSelectedRuntimeState()?.IsRunning == true;

    internal string? SelectedActivityText => GetSelectedRuntimeState()?.ActivityText;

    internal bool IsSelected(Guid conversationId) => _selectedConversationId == conversationId;

    internal bool IsRunning(Guid conversationId)
        => _runtimeStates.TryGetValue(conversationId, out var state) && state.IsRunning;

    internal IEnumerable<Guid> RunningConversationIds
        => _runtimeStates.Where(pair => pair.Value.IsRunning).Select(pair => pair.Key);

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

        var snapshot = await GetTranscriptSnapshotAsync(selectedId, cancellationToken);

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
        ForgetTranscriptLoad(state.ConversationId);
        if (IsSelected(state.ConversationId))
        {
            _selectionVersion++;
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

        ForgetTranscriptLoad(conversationId);
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
        _loadCancellation.Cancel();
        lock (_transcriptLoadGate) _transcriptLoads.Clear();
        _selectedTranscript = null;
        _loadCancellation.Dispose();
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
        if (IsSelected(conversationId) && _selectedTranscript is { } selected)
        {
            return Task.FromResult(selected);
        }

        TaskCompletionSource<ConversationTranscriptSnapshot> load;
        var ownsLoad = false;
        lock (_transcriptLoadGate)
        {
            if (!_transcriptLoads.TryGetValue(conversationId, out var pending))
            {
                pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _transcriptLoads.Add(conversationId, pending);
                ownsLoad = true;
            }

            load = pending;
        }

        if (ownsLoad) _ = CompleteTranscriptLoadAsync(conversationId, load);
        return load.Task.WaitAsync(cancellationToken);
    }

    private async Task CompleteTranscriptLoadAsync(Guid conversationId, TaskCompletionSource<ConversationTranscriptSnapshot> completion)
    {
        try
        {
            var snapshot = await LoadTranscriptAsync(conversationId, _loadCancellation.Token);
            ForgetTranscriptLoad(conversationId, completion);
            completion.TrySetResult(snapshot);
        }
        catch (OperationCanceledException exception)
        {
            ForgetTranscriptLoad(conversationId, completion);
            completion.TrySetCanceled(exception.CancellationToken);
            throw;
        }
        catch (Exception exception)
        {
            ForgetTranscriptLoad(conversationId, completion);
            completion.TrySetException(exception);
        }
    }

    private void ForgetTranscriptLoad(Guid conversationId, TaskCompletionSource<ConversationTranscriptSnapshot>? expected = null)
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
        => new(
            state.Messages.ToArray(),
            state.ToolRuns.ToArray());

    private static ConversationTranscriptSnapshot CreateSnapshot(
        IEnumerable<MessageRecord> messages,
        IEnumerable<ToolExecutionRecord> toolRuns)
    {
        var messageSnapshot = messages.ToArray();
        var toolRunSnapshot = toolRuns.ToArray();

        return new ConversationTranscriptSnapshot(messageSnapshot, toolRunSnapshot);
    }
}
