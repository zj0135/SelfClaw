using System.IO;
using System.Runtime.ExceptionServices;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;

namespace SelfClaw.Desktop.Services.ConversationInputs;

internal sealed class DirectTurnInputSession : IDirectTurnInputSession, IDirectTurnSteering
{
    private readonly Guid _conversationId;
    private readonly Guid _turnId;
    private readonly Guid _ownerRunId;
    private readonly IConversationInputRepository _repository;
    private readonly IConversationInputSteeringStore _steeringStore;
    private readonly ConversationInputChangeNotifier _changes;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _claimGate = new(1, 1);
    private ConversationInputBatch? _boundary;
    private TaskCompletionSource<ConversationInputConsumption>? _completion;
    private ConversationInputConsumption? _confirmed;
    private Exception? _failure;
    private CancellationToken _cancellationToken;
    private DirectTurnInputSessionState _state;
    private TaskCompletionSource _steerPulse = NewPulse();
    private string? _steerBlockReason;
    private bool _steerSignaled;
    private bool _steerIntakeClosed;
    private bool _reading;
    private bool _waitRegistered;

    public DirectTurnInputSession(
        Guid conversationId,
        Guid turnId,
        Guid ownerRunId,
        IConversationInputRepository repository,
        IConversationInputSteeringStore steeringStore,
        ConversationInputChangeNotifier changes)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(steeringStore);
        ArgumentNullException.ThrowIfNull(changes);
        if (conversationId == Guid.Empty || turnId == Guid.Empty || ownerRunId == Guid.Empty)
            throw new ArgumentException("An input session requires conversation, turn and run identities.");

        _conversationId = conversationId;
        _turnId = turnId;
        _ownerRunId = ownerRunId;
        _repository = repository;
        _steeringStore = steeringStore;
        _changes = changes;
    }

    internal int PendingWaiterCount
    {
        get
        {
            lock (_gate)
                return _completion is { Task.IsCompleted: false } ? 1 : 0;
        }
    }

    public string? SteerBlockReason
    {
        get { lock (_gate) return _steerBlockReason; }
    }

    public bool HasSupersedingSteer
    {
        get { lock (_gate) return _boundary is not null; }
    }

    internal bool SteerIntakeClosed
    {
        get { lock (_gate) return _steerIntakeClosed; }
    }

    public Task<ConversationInputBatch?> ReadBoundaryAsync(CancellationToken cancellationToken = default)
        => ReadBoundaryCoreAsync(closeWhenEmpty: false, cancellationToken);

    public Task<ConversationInputBatch?> ReadFinalBoundaryAsync(CancellationToken cancellationToken = default)
        => ReadBoundaryCoreAsync(closeWhenEmpty: true, cancellationToken);

    private async Task<ConversationInputBatch?> ReadBoundaryCoreAsync(bool closeWhenEmpty, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            ThrowIfTerminated();
            if (_boundary is not null && _confirmed is null)
            {
                RegisterBoundary(_boundary);
                return _boundary;
            }

            if (_reading) throw new InvalidOperationException("Only one boundary read may be in progress.");
            _boundary = null;
            _completion = null;
            _confirmed = null;
            _waitRegistered = false;
            _reading = true;
        }

        try
        {
            var batch = await TryClaimAsync(cancellationToken, closeWhenEmpty).ConfigureAwait(false);
            lock (_gate)
            {
                ThrowIfTerminated();
                if (batch is not null) RegisterBoundary(batch);
                return batch;
            }
        }
        catch (OperationCanceledException)
        {
            // Preserve the first terminal decision when enumerator teardown races a consumer failure.
            lock (_gate) ThrowIfTerminated();
            Cancel(cancellationToken);
            throw;
        }
        catch (Exception exception)
        {
            Fail(exception);
            throw;
        }
        finally
        {
            lock (_gate) _reading = false;
        }
    }

    public async Task<ConversationInputConsumption> WaitForCommitAsync(
        ConversationInputBatch batch,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        Task<ConversationInputConsumption> wait;
        bool reconfirm;
        lock (_gate)
        {
            ThrowIfTerminated();
            reconfirm = _waitRegistered || _boundary is null;
            RegisterBoundary(batch);
            _waitRegistered = true;
            wait = (_completion ?? throw new InvalidOperationException("The boundary has no completion slot.")).Task;
        }

        try
        {
            // A repeated wait repairs a lost in-process ACK from the existing consumption facts.
            // It never repeats the write transaction and does not resume a closed session.
            if (reconfirm && !wait.IsCompleted && await _repository.ReadConsumptionAsync(batch, cancellationToken) is { } committed)
                Confirm(committed);
            var result = await wait.WaitAsync(cancellationToken);
            lock (_gate) ThrowIfTerminated();
            return result;
        }
        catch (OperationCanceledException)
        {
            // Preserve the first terminal decision when enumerator teardown races a consumer failure.
            lock (_gate) ThrowIfTerminated();
            Cancel(cancellationToken);
            throw;
        }
        catch (Exception exception)
        {
            Fail(exception);
            throw;
        }
    }

    public async Task<bool> TryBeginToolExecutionAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate) ThrowIfTerminated();
        return await TryClaimAsync(cancellationToken).ConfigureAwait(false) is null;
    }

    /// <summary>
    /// Runs a steer acceptance under the same gate as the claim/close decision: either the accept
    /// commits before the close (and the close claims it) or it observes the closed intake and the
    /// caller falls back to a follow-up.
    /// </summary>
    internal async Task<T?> TryAcceptSteerAsync<T>(
        Func<CancellationToken, Task<T>> accept, CancellationToken cancellationToken) where T : class
    {
        await _claimGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_gate)
            {
                if (_state != DirectTurnInputSessionState.Open || _steerIntakeClosed) return null;
            }

            return await accept(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _claimGate.Release();
        }
    }

    public Task WaitForSteerPulseAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            ThrowIfTerminated();
            if (_steerSignaled)
            {
                // A pulse that landed before this wait still wakes it exactly once.
                _steerSignaled = false;
                return Task.CompletedTask;
            }

            return _steerPulse.Task.WaitAsync(cancellationToken);
        }
    }

    public async Task SetSteerPolicyAsync(string? blockReason, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            ThrowIfTerminated();
            if (_steerBlockReason == blockReason) return;
            _steerBlockReason = blockReason;
        }

        if (blockReason is not null)
        {
            var released = await _steeringStore.ReleasePendingSteersToFollowUpAsync(_conversationId, _turnId, blockReason, cancellationToken)
                .ConfigureAwait(false);
            // Released inputs are follow-ups again, so the published queue must show them that way.
            if (released > 0) _changes.Notify(_conversationId);
        }
    }

    internal void NotifySteerAccepted()
    {
        TaskCompletionSource pulse;
        lock (_gate)
        {
            if (_state != DirectTurnInputSessionState.Open) return;
            _steerSignaled = true;
            pulse = _steerPulse;
            _steerPulse = NewPulse();
        }

        pulse.TrySetResult();
    }

    internal async Task HoldUnconsumedSteersAsync(string reasonCode, CancellationToken cancellationToken = default)
    {
        if (await _steeringStore.HoldUnconsumedSteersAsync(_conversationId, _turnId, reasonCode, cancellationToken)
                .ConfigureAwait(false) > 0)
        {
            _changes.Notify(_conversationId);
        }
    }

    public Task HoldUnconsumedAsync(string reasonCode, CancellationToken cancellationToken = default)
        => HoldUnconsumedSteersAsync(reasonCode, cancellationToken);

    internal void Confirm(ConversationInputConsumption result)
    {
        ArgumentNullException.ThrowIfNull(result);
        bool first;
        lock (_gate)
        {
            if (_state != DirectTurnInputSessionState.Open || _boundary?.ClaimId != result.ClaimId) return;
            if (result.Turn.Id != _turnId || result.Turn.ConversationId != _conversationId ||
                result.Messages.Count != _boundary.Inputs.Count ||
                !result.Messages.Select(message => message.Id).SequenceEqual(_boundary.Inputs.Select(input => input.MessageId)) ||
                result.Messages.Any(message => message.TurnId != _turnId ||
                    message.ConversationId != _conversationId || message.Role != MessageRole.User))
                throw new InvalidDataException("The committed input mapping does not belong to this boundary.");

            first = _confirmed is null;
            _confirmed ??= result;
            _completion?.TrySetResult(_confirmed);
        }

        // Consumption is the moment a steer leaves the wait list for the transcript. Without an
        // invalidation the host keeps serving the pre-boundary snapshot and the card keeps showing
        // "等待生效" for an input the transcript already contains.
        if (first) _changes.Notify(_conversationId);
    }

    internal void Fail(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (exception is OperationCanceledException cancellation)
        {
            Cancel(cancellation.CancellationToken);
            return;
        }

        lock (_gate)
        {
            if (_state != DirectTurnInputSessionState.Open) return;
            _state = DirectTurnInputSessionState.Failed;
            _failure = exception;
            _completion?.TrySetException(exception);
            _steerPulse.TrySetResult();
        }
    }

    internal void Cancel(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_state != DirectTurnInputSessionState.Open) return;
            _state = DirectTurnInputSessionState.Cancelled;
            _cancellationToken = cancellationToken;
            _completion?.TrySetCanceled(cancellationToken);
            _steerPulse.TrySetResult();
        }
    }

    internal void Close()
    {
        lock (_gate)
        {
            if (_state != DirectTurnInputSessionState.Open) return;
            _state = DirectTurnInputSessionState.Closed;
            _completion?.TrySetException(new InvalidOperationException("The turn input session is closed."));
            _steerPulse.TrySetResult();
        }
    }

    private async Task<ConversationInputBatch?> TryClaimAsync(CancellationToken cancellationToken, bool closeWhenEmpty = false)
    {
        await _claimGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string? blockReason;
            lock (_gate)
            {
                ThrowIfTerminated();
                if (_boundary is not null) return _boundary;
                if (_steerIntakeClosed) return null;
                blockReason = _steerBlockReason;
            }

            if (blockReason is not null)
            {
                // A blocked turn never consumes a steer: anything accepted before the policy was
                // published becomes an ordinary follow-up instead of waiting forever.
                await _steeringStore.ReleasePendingSteersToFollowUpAsync(_conversationId, _turnId, blockReason, cancellationToken)
                    .ConfigureAwait(false);
                if (closeWhenEmpty) CloseIntake();
                return null;
            }

            var batch = await _steeringStore.TryClaimPendingSteersAsync(_conversationId, _turnId, _ownerRunId, cancellationToken)
                .ConfigureAwait(false);
            if (batch is null)
            {
                // The empty final claim is the close: a steer cannot enter the turn after it.
                if (closeWhenEmpty) CloseIntake();
                return null;
            }
            lock (_gate)
            {
                ThrowIfTerminated();
                _boundary ??= batch;
                return _boundary;
            }
        }
        finally
        {
            _claimGate.Release();
        }
    }

    private void CloseIntake()
    {
        lock (_gate) _steerIntakeClosed = true;
    }

    private void RegisterBoundary(ConversationInputBatch batch)
    {
        if (batch.ConversationId != _conversationId || batch.TurnId != _turnId || batch.OwnerRunId != _ownerRunId ||
            batch.ClaimId == Guid.Empty || batch.Inputs.Count == 0 ||
            batch.Inputs.Any(input => input.MessageId == Guid.Empty || input.MessageId == _turnId) ||
            batch.Inputs.Select(input => input.InputId).Distinct().Count() != batch.Inputs.Count ||
            batch.Inputs.Select(input => input.MessageId).Distinct().Count() != batch.Inputs.Count)
            throw new InvalidDataException("The input batch does not belong to this run.");

        if (_boundary is not null)
        {
            if (_boundary.ClaimId != batch.ClaimId)
                throw new InvalidOperationException("Only one pending boundary waiter per turn is allowed.");
            if (!_boundary.Inputs.SequenceEqual(batch.Inputs))
                throw new InvalidDataException("The claimed input batch changed while awaiting consumption.");
            _completion ??= new TaskCompletionSource<ConversationInputConsumption>(TaskCreationOptions.RunContinuationsAsynchronously);
            return;
        }

        _boundary = batch with { Inputs = batch.Inputs.ToArray() };
        _completion = new TaskCompletionSource<ConversationInputConsumption>(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private void ThrowIfTerminated()
    {
        if (_state == DirectTurnInputSessionState.Cancelled)
            throw new OperationCanceledException("The turn input session was cancelled.", _cancellationToken);
        if (_failure is not null) ExceptionDispatchInfo.Capture(_failure).Throw();
        if (_state == DirectTurnInputSessionState.Closed)
            throw new InvalidOperationException("The turn input session is closed.");
    }

    private static TaskCompletionSource NewPulse()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
