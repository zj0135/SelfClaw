using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Desktop.Services.Transcript.Abstractions;

namespace SelfClaw.Desktop.Services.Runtime;

internal sealed class ConversationSessionCoordinator : IDisposable
{
    private readonly IConversationRepository _conversations;
    private readonly IConversationTurnRepository _turns;
    private readonly ConversationRunCoordinator _runs;
    private readonly ITranscriptChangeSink _transcriptChangeSink;
    private readonly Dictionary<Guid, Task<ConversationTranscriptSnapshot>> _transcriptLoads = [];
    private readonly object _transcriptLoadGate = new();
    private readonly object _selectionGate = new();
    private readonly CancellationTokenSource _loadCancellation = new();
    private ConversationTranscriptSnapshot? _selectedTranscript;
    private Guid? _selectedConversationId;
    private long _selectedRunGeneration;
    private int _selectionVersion;
    private int _disposed;

    public ConversationSessionCoordinator(IConversationRepository conversations, IConversationTurnRepository turns,
        ConversationRunCoordinator runs, ITranscriptChangeSink transcriptChangeSink)
    {
        _conversations = conversations;
        _turns = turns;
        _runs = runs;
        _transcriptChangeSink = transcriptChangeSink;
        _runs.Changed += OnRunChanged;
    }

    internal IReadOnlyList<ConversationTurnRecord> SelectedTurns => CaptureSelectedTranscript().Turns;
    internal IReadOnlyList<MessageRecord> SelectedMessages => CaptureSelectedTranscript().Messages;
    internal bool IsSelectedRunning => GetSelectedRun() is not null;
    internal bool IsSelectedContinuation => GetSelectedRun()?.Origin == DirectTurnOrigin.Continuation;
    internal string? SelectedActivityText => GetSelectedRun()?.RuntimeState?.ActivityText;
    internal event Action? SelectedStateChanged;

    internal ConversationTranscriptSnapshot CaptureSelectedTranscript()
    {
        lock (_selectionGate)
            return GetSelectedRun()?.RuntimeState?.CaptureSnapshot() ?? _selectedTranscript ?? new([], [], []);
    }

    internal bool IsSelected(Guid conversationId)
    {
        lock (_selectionGate) return _selectedConversationId == conversationId;
    }

    internal async Task SelectAsync(Guid? conversationId, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        int version;
        lock (_selectionGate)
        {
            version = ++_selectionVersion;
            _selectedConversationId = conversationId;
            _selectedTranscript = null;
            _selectedRunGeneration = GetSelectedRun()?.Generation ?? 0;
        }
        _transcriptChangeSink.PublishNow(conversationId is not null);
        if (conversationId is not Guid selectedId || _runs.GetActiveRun(selectedId)?.RuntimeState is not null) return;
        var snapshot = await GetTranscriptSnapshotAsync(selectedId, cancellationToken);
        lock (_selectionGate)
        {
            if (version != _selectionVersion || _selectedConversationId != selectedId || Volatile.Read(ref _disposed) != 0) return;
            _selectedTranscript = snapshot;
        }
        _transcriptChangeSink.PublishNow(true);
    }

    internal async Task<ConversationRuntimeState> PrepareRuntimeStateAsync(ConversationRecord conversation,
        bool isDetached, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        var snapshot = await GetTranscriptSnapshotAsync(conversation.Id, cancellationToken);
        return new(conversation, snapshot.Turns, snapshot.Messages, snapshot.ToolRuns, isDetached);
    }

    internal void ForgetTranscript(Guid conversationId)
    {
        ForgetTranscriptLoad(conversationId);
        lock (_selectionGate)
        {
            if (_selectedConversationId != conversationId) return;
            _selectionVersion++;
            _selectedTranscript = null;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _runs.Changed -= OnRunChanged;
        _loadCancellation.Cancel();
        lock (_transcriptLoadGate) _transcriptLoads.Clear();
        lock (_selectionGate)
        {
            _selectionVersion++;
            _selectedConversationId = null;
            _selectedTranscript = null;
        }
        _loadCancellation.Dispose();
    }

    private void OnRunChanged(ConversationRunChange change)
    {
        var handle = change.Handle;
        var state = handle.RuntimeState;
        if (!change.IsCompleted && state is not null)
        {
            state.TranscriptChanged += immediate =>
            {
                if (IsSelected(handle.ConversationId) && ReferenceEquals(_runs.GetActiveRun(handle.ConversationId), handle))
                    PublishSelectedTranscriptChange(immediate);
            };
        }
        if (change.IsCompleted) ForgetTranscriptLoad(handle.ConversationId);
        lock (_selectionGate)
        {
            if (_selectedConversationId != handle.ConversationId || handle.Generation < _selectedRunGeneration) return;
            var active = _runs.GetActiveRun(handle.ConversationId);
            if (active is not null && active.Generation > handle.Generation) return;
            _selectedRunGeneration = handle.Generation;
            if (state is not null) _selectionVersion++;
            if (change.IsCompleted && state is not null)
                _selectedTranscript = change.PublishPersistedContent ? state.CaptureSnapshot() : state.InitialSnapshot;
        }
        _transcriptChangeSink.PublishNow(true);
        SelectedStateChanged?.Invoke();
    }

    private ConversationRunHandle? GetSelectedRun()
    {
        lock (_selectionGate)
            return _selectedConversationId is Guid id ? _runs.GetActiveRun(id) : null;
    }

    private void PublishSelectedTranscriptChange(bool immediate)
    {
        if (immediate) _transcriptChangeSink.PublishNow(true);
        else _transcriptChangeSink.RequestStreamingPublish(true);
    }

    private Task<ConversationTranscriptSnapshot> GetTranscriptSnapshotAsync(Guid conversationId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        lock (_selectionGate)
        {
            if (_selectedConversationId == conversationId && _selectedTranscript is { } selected) return Task.FromResult(selected);
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
                }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
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

    private async Task<ConversationTranscriptSnapshot> LoadTranscriptAsync(Guid conversationId, CancellationToken cancellationToken)
    {
        var turnsTask = _turns.ListTurnsAsync(conversationId, cancellationToken);
        var messagesTask = _conversations.ListMessagesAsync(conversationId, cancellationToken);
        var toolsTask = _conversations.ListToolExecutionsAsync(conversationId, cancellationToken);
        await Task.WhenAll(turnsTask, messagesTask, toolsTask);
        return new(await turnsTask, await messagesTask, await toolsTask);
    }
}
