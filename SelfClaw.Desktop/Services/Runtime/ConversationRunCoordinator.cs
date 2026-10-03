using Microsoft.Extensions.Logging;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Desktop.Services.ConversationInputs;

namespace SelfClaw.Desktop.Services.Runtime;

internal sealed class ConversationRunCoordinator : IDisposable
{
    private readonly IConversationInputRepository _inputs;
    private readonly ILogger<ConversationRunCoordinator> _logger;
    private readonly object _gate = new();
    private readonly Dictionary<Guid, ConversationRunHandle> _runs = [];
    private readonly Dictionary<Guid, ConversationDeletionReservation?> _deletions = [];
    private readonly HashSet<ConversationInputOperation> _inputOperations = [];
    private long _generation;
    private bool _stopping;

    public ConversationRunCoordinator(IConversationInputRepository inputs, ILogger<ConversationRunCoordinator> logger)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(logger);
        _inputs = inputs;
        _logger = logger;
    }

    internal event Action<ConversationRunChange>? Changed;

    internal ConversationInputOperation? TryBeginInputOperation(Guid conversationId, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_stopping || _deletions.ContainsKey(conversationId)) return null;
            var operation = new ConversationInputOperation(this, conversationId, token);
            _inputOperations.Add(operation);
            return operation;
        }
    }

    internal void CompleteInputOperation(ConversationInputOperation operation)
    {
        lock (_gate) _inputOperations.Remove(operation);
    }

    internal async Task DrainInputOperationsAsync(Guid conversationId, TimeSpan timeout, CancellationToken token = default)
    {
        ConversationInputOperation[] operations;
        lock (_gate) operations = _inputOperations.Where(item => item.ConversationId == conversationId).ToArray();
        foreach (var operation in operations) operation.Cancel();
        await Task.WhenAll(operations.Select(item => item.Completion)).WaitAsync(timeout, token).ConfigureAwait(false);
    }

    internal ConversationRunHandle? TryReserve(Guid conversationId, AgentExecutionMode mode,
        DirectTurnOrigin origin, CancellationToken cancellationToken = default)
    {
        if (conversationId == Guid.Empty) throw new ArgumentException("A run requires a conversation id.", nameof(conversationId));
        if (origin == DirectTurnOrigin.Subagent)
            throw new ArgumentException("Child runs have their own lifecycle owner.", nameof(origin));
        cancellationToken.ThrowIfCancellationRequested();
        ConversationRunHandle handle;
        lock (_gate)
        {
            if (_stopping || _deletions.ContainsKey(conversationId) || _runs.ContainsKey(conversationId)) return null;
            var runId = Guid.NewGuid();
            var turnId = Guid.NewGuid();
            var input = mode == AgentExecutionMode.Direct && origin == DirectTurnOrigin.Interactive
                ? new DirectTurnInputSession(conversationId, turnId, runId, _inputs) : null;
            handle = new ConversationRunHandle(conversationId, mode, origin, ++_generation, input, runId, turnId, cancellationToken);
            _runs.Add(conversationId, handle);
        }
        try
        {
            Publish(new(handle, false));
            return handle;
        }
        catch (OperationCanceledException)
        {
            Complete(handle, false);
            throw;
        }
    }

    internal async Task<ConversationRunHandle?> TryReserveContinuationAsync(ConversationRecord conversation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        if (conversation.Kind != ConversationKind.Interactive) return null;
        var handle = TryReserve(conversation.Id, AgentExecutionMode.Direct, DirectTurnOrigin.Continuation, cancellationToken);
        if (handle is null) return null;
        var admitted = false;
        try
        {
            if (await _inputs.HasUnprocessedInputsAsync(conversation.Id, handle.CancellationToken).ConfigureAwait(false)) return null;
            handle.CancellationToken.ThrowIfCancellationRequested();
            admitted = true;
            return handle;
        }
        finally
        {
            if (!admitted) Complete(handle, false);
        }
    }

    internal void AttachState(ConversationRunHandle handle, ConversationRuntimeState state)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentNullException.ThrowIfNull(state);
        lock (_gate)
        {
            RequireOwner(handle);
            if (state.ConversationId != handle.ConversationId || state.IsDetached != (handle.Origin == DirectTurnOrigin.Continuation))
                throw new InvalidOperationException("The transcript does not belong to this run.");
            handle.AttachState(state);
        }
        Publish(new(handle, false));
    }

    internal void BeginExecution(ConversationRunHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        lock (_gate)
        {
            RequireOwner(handle);
            handle.BeginExecution();
        }
    }

    internal bool Complete(ConversationRunHandle handle, bool publishPersistedContent = true)
    {
        ArgumentNullException.ThrowIfNull(handle);
        lock (_gate)
        {
            if (!_runs.TryGetValue(handle.ConversationId, out var current) || !ReferenceEquals(current, handle) ||
                !handle.TryBeginCompletion()) return false;
        }
        try { handle.ReleaseResources(); }
        finally
        {
            lock (_gate) _runs.Remove(handle.ConversationId);
            try { Publish(new(handle, true, publishPersistedContent)); }
            finally { handle.MarkCompleted(); }
        }
        return true;
    }

    internal ConversationRunHandle? GetActiveRun(Guid conversationId)
    {
        lock (_gate) return _runs.GetValueOrDefault(conversationId);
    }

    internal bool IsRunning(Guid conversationId) => GetActiveRun(conversationId) is not null;

    internal IReadOnlyCollection<Guid> RunningConversationIds
    {
        get { lock (_gate) return _runs.Keys.ToArray(); }
    }

    internal IReadOnlyCollection<Guid> GetUnavailableContinuationParents()
    {
        lock (_gate) return _runs.Keys.Concat(_deletions.Keys).Distinct().ToArray();
    }

    internal void Stop(Guid conversationId) => GetActiveRun(conversationId)?.Cancel();

    internal async Task StopAndWaitAsync(Guid conversationId, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var handle = GetActiveRun(conversationId);
        if (handle is null) return;
        handle.Cancel();
        await handle.Completion.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
    }

    internal ConversationDeletionReservation BeginDeletion(Guid conversationId)
    {
        if (conversationId == Guid.Empty) throw new ArgumentException("A deletion requires a conversation id.", nameof(conversationId));
        lock (_gate)
        {
            if (_deletions.ContainsKey(conversationId)) throw new InvalidOperationException("This conversation is already being deleted.");
            var reservation = new ConversationDeletionReservation(conversationId, Guid.NewGuid());
            _deletions.Add(conversationId, reservation);
            return reservation;
        }
    }

    internal void EndDeletion(ConversationDeletionReservation reservation, bool deleted)
    {
        ArgumentNullException.ThrowIfNull(reservation);
        lock (_gate)
        {
            if (!_deletions.TryGetValue(reservation.ConversationId, out var current) || !ReferenceEquals(current, reservation)) return;
            // A completed tombstone has no releasable reservation, even if old cleanup runs again.
            if (deleted) _deletions[reservation.ConversationId] = null;
            else _deletions.Remove(reservation.ConversationId);
        }
    }

    internal void StopAdmissions()
    {
        lock (_gate) _stopping = true;
    }

    internal async Task StopAsync(CancellationToken cancellationToken)
    {
        ConversationRunHandle[] handles;
        ConversationInputOperation[] operations;
        lock (_gate)
        {
            _stopping = true;
            handles = _runs.Values.ToArray();
            operations = _inputOperations.ToArray();
        }
        foreach (var operation in operations) operation.Cancel();
        foreach (var handle in handles) handle.Cancel();
        await Task.WhenAll(handles.Select(handle => handle.Completion).Concat(operations.Select(item => item.Completion)))
            .WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        ConversationRunHandle[] handles;
        ConversationInputOperation[] operations;
        lock (_gate)
        {
            _stopping = true;
            handles = _runs.Values.ToArray();
            operations = _inputOperations.ToArray();
        }
        foreach (var operation in operations) operation.Cancel();
        foreach (var handle in handles) handle.Cancel();
    }

    private void RequireOwner(ConversationRunHandle handle)
    {
        if (!_runs.TryGetValue(handle.ConversationId, out var current) || !ReferenceEquals(current, handle) || handle.IsCompleting)
            throw new InvalidOperationException("The run handle no longer owns this conversation.");
    }

    private void Publish(ConversationRunChange change)
    {
        if (Changed is not { } handlers) return;
        foreach (Action<ConversationRunChange> handler in handlers.GetInvocationList())
        {
            try { handler(change); }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception) { _logger.LogError(exception, "Run projection notification failed for {RunId}.", change.Handle.RunId); }
        }
    }
}
