using System.IO;
using System.Runtime.ExceptionServices;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;

namespace SelfClaw.Desktop.Services.ConversationInputs;

internal sealed class DirectTurnInputSession : IDirectTurnInputSession
{
    private readonly Guid _conversationId;
    private readonly Guid _turnId;
    private readonly Guid _ownerRunId;
    private readonly IConversationInputRepository _repository;
    private readonly object _gate = new();
    private ConversationInputBatch? _boundary;
    private TaskCompletionSource<ConversationInputConsumption>? _completion;
    private ConversationInputConsumption? _confirmed;
    private Exception? _failure;
    private CancellationToken _cancellationToken;
    private DirectTurnInputSessionState _state;
    private bool _reading;
    private bool _waitRegistered;

    public DirectTurnInputSession(
        Guid conversationId,
        Guid turnId,
        Guid ownerRunId,
        IConversationInputRepository repository)
    {
        ArgumentNullException.ThrowIfNull(repository);
        if (conversationId == Guid.Empty || turnId == Guid.Empty || ownerRunId == Guid.Empty)
            throw new ArgumentException("An input session requires conversation, turn and run identities.");

        _conversationId = conversationId;
        _turnId = turnId;
        _ownerRunId = ownerRunId;
        _repository = repository;
    }

    internal int PendingWaiterCount
    {
        get
        {
            lock (_gate)
                return _completion is { Task.IsCompleted: false } ? 1 : 0;
        }
    }

    public async Task<ConversationInputBatch?> ReadBoundaryAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            ThrowIfTerminated();
            if (_boundary is not null && _confirmed is null) return _boundary;
            if (_reading) throw new InvalidOperationException("Only one boundary read may be in progress.");
            _boundary = null;
            _completion = null;
            _confirmed = null;
            _waitRegistered = false;
            _reading = true;
        }

        try
        {
            var batch = await _repository.ReadClaimedBatchAsync(
                _conversationId, _turnId, _ownerRunId, cancellationToken);
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

    internal void Confirm(ConversationInputConsumption result)
    {
        ArgumentNullException.ThrowIfNull(result);
        lock (_gate)
        {
            if (_state != DirectTurnInputSessionState.Open || _boundary?.ClaimId != result.ClaimId) return;
            if (result.Turn.Id != _turnId || result.Turn.ConversationId != _conversationId ||
                result.Messages.Count != _boundary.Inputs.Count ||
                !result.Messages.Select(message => message.Id).SequenceEqual(_boundary.Inputs.Select(input => input.MessageId)) ||
                result.Messages.Any(message => message.TurnId != _turnId ||
                    message.ConversationId != _conversationId || message.Role != MessageRole.User))
                throw new InvalidDataException("The committed input mapping does not belong to this boundary.");

            _confirmed ??= result;
            _completion?.TrySetResult(_confirmed);
        }
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
        }
    }

    internal void Close()
    {
        lock (_gate)
        {
            if (_state != DirectTurnInputSessionState.Open) return;
            _state = DirectTurnInputSessionState.Closed;
            _completion?.TrySetException(new InvalidOperationException("The turn input session is closed."));
        }
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
}
