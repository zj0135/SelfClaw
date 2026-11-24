using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Desktop.Services.Transcript.Abstractions;
using SelfClaw.Desktop.Services.Transcript.Views;

namespace SelfClaw.Desktop.Services.Runtime;

internal sealed class ConversationSessionCoordinator : IDisposable
{
    private readonly IConversationRepository _conversations;
    private readonly IConversationTurnRepository _turns;
    private readonly IConversationCompactionRepository _compactions;
    private readonly ConversationRunCoordinator _runs;
    private readonly ITranscriptChangeSink _transcriptChangeSink;
    private readonly Dictionary<Guid, Task<ConversationTranscriptSnapshot>> _transcriptLoads = [];
    private readonly object _transcriptLoadGate = new();
    private readonly object _selectionGate = new();
    // The trigger a conversation's last turn compacted at, kept so the usage meter still shows the mark between
    // turns. It is memory only: a conversation that has not run since the app started simply shows none. Only
    // interactive and continuation runs pass through the run coordinator, so no child run leaves an entry here.
    private readonly Dictionary<Guid, int> _compactionTriggers = [];
    private readonly CancellationTokenSource _loadCancellation = new();
    private ConversationTranscriptSnapshot? _selectedTranscript;
    private Guid? _selectedConversationId;
    private long _selectedRunGeneration;
    private int _selectionVersion;
    private int _disposed;

    public ConversationSessionCoordinator(IConversationRepository conversations, IConversationTurnRepository turns,
        IConversationCompactionRepository compactions, ConversationRunCoordinator runs, ITranscriptChangeSink transcriptChangeSink)
    {
        _conversations = conversations;
        _turns = turns;
        _compactions = compactions;
        _runs = runs;
        _transcriptChangeSink = transcriptChangeSink;
        _runs.Changed += OnRunChanged;
    }

    internal IReadOnlyList<ConversationTurnRecord> SelectedTurns => CaptureSelectedTranscript().Turns;
    internal IReadOnlyList<MessageRecord> SelectedMessages => CaptureSelectedTranscript().Messages;
    internal bool IsSelectedRunning => GetSelectedRun() is not null;
    internal bool IsSelectedContinuation => GetSelectedRun()?.Origin == DirectTurnOrigin.Continuation;
    internal string? SelectedActivityText => GetSelectedRun()?.RuntimeState?.ActivityText;
    internal TranscriptLiveUsage? SelectedLiveUsage => GetSelectedRun()?.RuntimeState?.LiveUsage;

    internal int? SelectedCompactionTriggerTokens
    {
        get
        {
            lock (_selectionGate)
            {
                if (_selectedConversationId is not Guid id) return null;
                if (_runs.GetActiveRun(id)?.RuntimeState?.CompactionTriggerTokens is int running) return running;
                return _compactionTriggers.TryGetValue(id, out var remembered) ? remembered : null;
            }
        }
    }

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
        return new(conversation, snapshot.Turns, snapshot.Messages, snapshot.ToolRuns, snapshot.Compactions, isDetached);
    }

    internal void ForgetTranscript(Guid conversationId)
    {
        ForgetTranscriptLoad(conversationId);
        lock (_selectionGate)
        {
            _compactionTriggers.Remove(conversationId);
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
            if (change.IsCompleted && state is not null)
            {
                // A turn that resolved no trigger (compaction off, a CLI turn) clears the mark instead of
                // leaving an older turn's behind.
                if (state.CompactionTriggerTokens is int trigger) _compactionTriggers[handle.ConversationId] = trigger;
                else _compactionTriggers.Remove(handle.ConversationId);
            }
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
        var compactionsTask = _compactions.ListAsync(conversationId, cancellationToken);
        await Task.WhenAll(turnsTask, messagesTask, toolsTask, compactionsTask);
        return new(await turnsTask, await messagesTask, await toolsTask, await compactionsTask);
    }
}
