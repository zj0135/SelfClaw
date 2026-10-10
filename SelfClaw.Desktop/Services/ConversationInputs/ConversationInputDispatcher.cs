using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Desktop.Services.Runtime;

namespace SelfClaw.Desktop.Services.ConversationInputs;

/// <summary>
/// Discovers dispatchable conversations, asks the run coordinator for admission, claims the FIFO
/// head and runs it. Any non-successful outcome pauses the queue before the run releases admission.
/// </summary>
internal sealed class ConversationInputDispatcher : BackgroundService
{
    private static readonly TimeSpan ScanInterval = TimeSpan.FromMilliseconds(250);
    private readonly IConversationInputStore _store;
    private readonly IConversationInputSchedulerStore _scheduler;
    private readonly IConversationRepository _conversations;
    private readonly ConversationRunCoordinator _runs;
    private readonly ConversationTurnEngine _engine;
    private readonly ConversationInputExecutionValidator _validator;
    private readonly ConversationInputChangeNotifier _changes;
    private readonly ILogger<ConversationInputDispatcher> _logger;
    private readonly Func<CancellationToken, Task> _recover;
    private readonly object _taskGate = new();
    private readonly object _recoveryGate = new();
    private readonly HashSet<Task> _running = [];
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _recovery;

    public ConversationInputDispatcher(IConversationInputStore store, IConversationInputSchedulerStore scheduler,
        IConversationRepository conversations, ConversationRunCoordinator runs, ConversationTurnEngine engine,
        ConversationInputExecutionValidator validator, ConversationInputChangeNotifier changes,
        ILogger<ConversationInputDispatcher> logger, Func<CancellationToken, Task>? recover = null)
    {
        _store = store;
        _scheduler = scheduler;
        _conversations = conversations;
        _runs = runs;
        _engine = engine;
        _validator = validator;
        _changes = changes;
        _logger = logger;
        _recover = recover ?? (_ => Task.CompletedTask);
    }

    internal void RequestStart(Guid conversationId, Guid? immediateInputId = null, ConversationRunHandle? reserved = null)
    {
        lock (_taskGate)
        {
            if (_shutdown.IsCancellationRequested)
            {
                if (reserved is not null) _runs.Complete(reserved, false);
                return;
            }
            Track(Task.Run(() => TryStartConversationAsync(conversationId, _shutdown.Token, immediateInputId, reserved)));
        }
    }

    internal Task RecoverOnceAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_recoveryGate) return _recovery ??= _recover(CancellationToken.None);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ScanOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception) { _logger.LogError(exception, "Input queue scan failed."); }
            await Task.Delay(ScanInterval, stoppingToken).ConfigureAwait(false);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _runs.StopAdmissions();
        _shutdown.Cancel();
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
        Task[] tasks;
        lock (_taskGate) tasks = _running.ToArray();
        await Task.WhenAll(tasks.Select(task => task.ContinueWith(_ => { }, TaskScheduler.Default)))
            .WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    internal async Task<bool> ScanOnceAsync(CancellationToken token)
    {
        var ids = await _scheduler.ListDispatchableConversationIdsAsync(token).ConfigureAwait(false);
        var progressed = false;
        // The durable application cap is 500; visiting every parent prevents paused heads starving others.
        foreach (var id in ids)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var attempt = await TryStartConversationAsync(id, token).ConfigureAwait(false);
                progressed |= attempt.Status == ConversationInputStartStatus.Started;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception) { _logger.LogError(exception, "Input preparation failed for {ConversationId}.", id); }
        }
        return progressed;
    }

    internal async Task<ConversationInputStartAttempt> TryStartConversationAsync(Guid conversationId, CancellationToken token,
        Guid? immediateInputId = null, ConversationRunHandle? reserved = null)
    {
        await RecoverOnceAsync(token).ConfigureAwait(false);
        if (_shutdown.IsCancellationRequested || conversationId == Guid.Empty ||
            (reserved is null && _runs.IsRunning(conversationId)))
        {
            if (reserved is not null) _runs.Complete(reserved, false);
            return new(ConversationInputStartStatus.Idle);
        }
        // Never reserve a run handle for work that cannot be claimed: a paused queue or a non-Pending
        // FIFO head would otherwise churn admission on every scan and republish the transcript.
        var preview = await _store.GetQueueStateAsync(conversationId, token).ConfigureAwait(false);
        if (!IsDispatchable(preview, immediateInputId))
        {
            if (reserved is not null) _runs.Complete(reserved, false);
            return new(ConversationInputStartStatus.Blocked);
        }
        var handle = reserved ?? _runs.TryReserve(conversationId, AgentExecutionMode.Direct, DirectTurnOrigin.Interactive, token);
        if (handle is null) return new(ConversationInputStartStatus.Idle);
        var handedOff = false;
        try
        {
            var attempt = await ClaimAndLaunchAsync(handle, immediateInputId).ConfigureAwait(false);
            handedOff = attempt.Status == ConversationInputStartStatus.Started;
            return attempt;
        }
        finally { if (!handedOff) _runs.Complete(handle, false); }
    }

    private bool IsDispatchable(ConversationInputQueueState state, Guid? immediateInputId)
    {
        if (state.Items.Count == 0 || state.Items[0] is not { Kind: ConversationInputKind.FollowUp, Status: ConversationInputStatus.Pending } head)
            return false;
        // A send made while idle runs at once when nothing waits ahead of it: a pause only holds back
        // items queued before it.
        if (head.InputId == immediateInputId) return true;
        return !state.Paused;
    }

    private async Task<ConversationInputStartAttempt> ClaimAndLaunchAsync(ConversationRunHandle handle, Guid? immediateInputId)
    {
        var token = handle.CancellationToken;
        var conversation = await _conversations.GetConversationAsync(handle.ConversationId, token).ConfigureAwait(false);
        if (conversation is not { Kind: ConversationKind.Interactive }) return new(ConversationInputStartStatus.Idle);
        var claimed = await _scheduler.TryClaimNextFollowUpAsync(handle.ConversationId, handle.RunId, Guid.NewGuid(),
            immediateInputId, token).ConfigureAwait(false);
        if (claimed is null) return new(ConversationInputStartStatus.Blocked);
        try
        {
            if (claimed.ExecutionSnapshot is not { } snapshot || claimed.ClaimId is not { } claimId)
                return await HoldAsync(claimed, ConversationInputReason.AgentChanged).ConfigureAwait(false);
            var preparation = await _validator.PrepareAsync(conversation, snapshot, token).ConfigureAwait(false);
            if (preparation.FollowUp is not { } prepared)
                return await HoldAsync(claimed, preparation.Reason ?? ConversationInputReason.ExecutionStartFailed).ConfigureAwait(false);
            var claim = new ConversationInputClaim(claimed.Id, conversation.Id, claimed.Revision, claimId,
                handle.RunId, ConversationInputMessageId.Compute(claimId, claimed.Id));
            var request = new DesktopConversationTurnRequest(conversation, prepared.Agent, claimed.Prompt,
                snapshot.ModelProfileId, prepared.WorkspaceRoot, snapshot.ToolPermissionMode, conversation.Id, claim);
            lock (_taskGate)
            {
                _shutdown.Token.ThrowIfCancellationRequested();
                Track(Task.Run(() => RunAsync(handle, request), CancellationToken.None));
            }
            _changes.Notify(conversation.Id);
            return new(ConversationInputStartStatus.Started, claimed.Id);
        }
        catch (OperationCanceledException)
        {
            await HoldAsync(claimed, ConversationInputReason.TurnCancelled).ConfigureAwait(false);
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed to start input {InputId}.", claimed.Id);
            return await HoldAsync(claimed, ConversationInputReason.ExecutionStartFailed).ConfigureAwait(false);
        }
    }

    private async Task<ConversationInputStartAttempt> HoldAsync(ConversationInputRecord input, string reason)
    {
        await _scheduler.HoldAsync(input.Id, input.Revision, reason, CancellationToken.None).ConfigureAwait(false);
        await PauseQueueAsync(input.ConversationId, reason).ConfigureAwait(false);
        return new(ConversationInputStartStatus.Failed, input.Id, reason);
    }

    private async Task PauseQueueAsync(Guid conversationId, string reason)
    {
        await _scheduler.PauseQueueAsync(conversationId, reason, CancellationToken.None).ConfigureAwait(false);
        _changes.Notify(conversationId);
    }

    private async Task RunAsync(ConversationRunHandle handle, DesktopConversationTurnRequest request)
    {
        var succeeded = false;
        try
        {
            try
            {
                var result = await _engine.ExecuteAsync(handle, request).ConfigureAwait(false);
                succeeded = result.Persisted && result.Turn?.Status == ConversationTurnStatus.Succeeded;
                if (!succeeded && !IsUserCancellation(handle, MapFailureReason(result)))
                    await PauseBeforeReleaseAsync(handle.ConversationId, MapFailureReason(result)).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                var reason = handle.ExecutionResult?.Persisted == true
                    ? ConversationInputReason.TurnCancelled : ConversationInputReason.PersistFailed;
                // A clean user stop already discarded the queue and lifted the pause: re-pausing would
                // strand the next send behind a manual resume. A failed terminal commit still pauses,
                // because the turn's cancelled state was never durably recorded.
                if (!IsUserCancellation(handle, reason))
                    await PauseBeforeReleaseAsync(handle.ConversationId, reason).ConfigureAwait(false);

                throw;
            }
            catch (Exception)
            {
                await PauseBeforeReleaseAsync(handle.ConversationId, ConversationInputReason.PersistFailed).ConfigureAwait(false);
                throw;
            }
        }
        finally { _runs.Complete(handle); }

        _changes.Notify(handle.ConversationId);
        if (succeeded && !_shutdown.IsCancellationRequested)
            await TryStartConversationAsync(handle.ConversationId, _shutdown.Token).ConfigureAwait(false);
    }

    private async Task PauseBeforeReleaseAsync(Guid conversationId, string reason)
    {
        while (true)
        {
            try { await PauseQueueAsync(conversationId, reason).ConfigureAwait(false); return; }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Keeping input admission until {ConversationId} is durably paused.", conversationId);
                // Shutdown stops coordinator admission before cancelling this retry.
                await Task.Delay(ScanInterval, _shutdown.Token).ConfigureAwait(false);
            }
        }
    }

    private void Track(Task task)
    {
        _running.Add(task);
        _ = task.ContinueWith(ObserveCompletion, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }

    private void ObserveCompletion(Task task)
    {
        if (task.Exception is { } error) _logger.LogError(error, "Input dispatch task failed.");
        lock (_taskGate) _running.Remove(task);
    }

    private static string MapFailureReason(ConversationTurnExecutionResult result)
    {
        if (!result.Persisted) return ConversationInputReason.PersistFailed;
        return result.Turn?.Status switch
        {
            ConversationTurnStatus.Cancelled => ConversationInputReason.TurnCancelled,
            ConversationTurnStatus.Truncated => ConversationInputReason.TurnTruncated,
            ConversationTurnStatus.Blocked => ConversationInputReason.TurnBlocked,
            ConversationTurnStatus.Interrupted => ConversationInputReason.TurnInterrupted,
            ConversationTurnStatus.Failed => ConversationInputReason.TurnFailed,
            _ => ConversationInputReason.PersistFailed
        };
    }

    private static bool IsUserCancellation(ConversationRunHandle handle, string reason)
        => handle.UserInitiatedStop && reason == ConversationInputReason.TurnCancelled;
}
