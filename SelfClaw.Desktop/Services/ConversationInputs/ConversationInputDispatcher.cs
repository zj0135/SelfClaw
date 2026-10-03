using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Desktop.Services.Agents;
using SelfClaw.Desktop.Services.Runtime;

namespace SelfClaw.Desktop.Services.ConversationInputs;

internal sealed class ConversationInputDispatcher : BackgroundService
{
    private static readonly TimeSpan ScanInterval = TimeSpan.FromMilliseconds(250);
    private readonly IConversationInputStore _store;
    private readonly IConversationInputSchedulerStore _scheduler;
    private readonly IConversationRepository _conversations;
    private readonly ConversationRunCoordinator _runs;
    private readonly ConversationTurnEngine _engine;
    private readonly ConversationInputExecutionValidator _validator;
    private readonly ConversationInputFeatureSwitch _featureSwitch;
    private readonly ConversationInputChangeNotifier _changes;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ConversationInputDispatcher> _logger;
    private readonly object _taskGate = new();
    private readonly HashSet<Task> _running = [];
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _recovery;

    public ConversationInputDispatcher(IConversationInputStore store, IConversationInputSchedulerStore scheduler,
        IConversationRepository conversations, IWorkspaceRootRepository roots, ConversationRunCoordinator runs,
        ConversationTurnEngine engine, IAiModelCatalog models, AgentSettingsService agents,
        ConversationInputFeatureSwitch featureSwitch, ConversationInputChangeNotifier changes,
        ILogger<ConversationInputDispatcher> logger)
        : this(store, scheduler, conversations, roots, runs, engine, models, agents, featureSwitch, changes, TimeProvider.System, logger) { }

    internal ConversationInputDispatcher(IConversationInputStore store, IConversationInputSchedulerStore scheduler,
        IConversationRepository conversations, IWorkspaceRootRepository roots, ConversationRunCoordinator runs,
        ConversationTurnEngine engine, IAiModelCatalog models, AgentSettingsService agents,
        ConversationInputFeatureSwitch featureSwitch, ConversationInputChangeNotifier changes,
        TimeProvider timeProvider, ILogger<ConversationInputDispatcher> logger)
    {
        _store = store;
        _scheduler = scheduler;
        _conversations = conversations;
        _runs = runs;
        _engine = engine;
        _validator = new(roots, models, agents);
        _featureSwitch = featureSwitch;
        _changes = changes;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    internal Task RecoverOnceAsync(CancellationToken token)
    {
        Task recovery;
        lock (_taskGate) recovery = _recovery ??= Task.Run(() => _scheduler.RecoverStartupAsync(_shutdown.Token));
        return recovery.WaitAsync(token);
    }

    internal void RequestStart(Guid conversationId, Guid? immediateInputId = null)
    {
        lock (_taskGate)
        {
            if (_shutdown.IsCancellationRequested) return;
            var task = Task.Run(() => TryStartConversationAsync(conversationId, _shutdown.Token, immediateInputId));
            _running.Add(task);
            _ = task.ContinueWith(ObserveCompletion, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RecoverOnceAsync(stoppingToken).ConfigureAwait(false);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (_featureSwitch.QueueEnabled) await ScanOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception) { _logger.LogError(exception, "Input queue scan failed."); }
            await Task.Delay(ScanInterval, _timeProvider, stoppingToken).ConfigureAwait(false);
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
        Guid? immediateInputId = null)
    {
        await RecoverOnceAsync(token).ConfigureAwait(false);
        if (_shutdown.IsCancellationRequested || conversationId == Guid.Empty || _runs.IsRunning(conversationId))
            return new(ConversationInputStartStatus.Idle);
        if (!_featureSwitch.QueueEnabled && immediateInputId is null) return new(ConversationInputStartStatus.Blocked);
        // Never reserve a run handle for work that cannot be claimed: a paused queue or a non-Pending
        // FIFO head would otherwise churn admission on every scan and republish the transcript.
        var preview = await _store.GetQueueStateAsync(conversationId, token).ConfigureAwait(false);
        if (!IsDispatchable(preview, immediateInputId)) return new(ConversationInputStartStatus.Blocked);
        var handle = _runs.TryReserve(conversationId, AgentExecutionMode.Direct, DirectTurnOrigin.Interactive, token);
        if (handle is null) return new(ConversationInputStartStatus.Idle);
        var handedOff = false;
        ConversationInputRecord? claimed = null;
        try
        {
            var conversation = await _conversations.GetConversationAsync(conversationId, handle.CancellationToken).ConfigureAwait(false);
            if (conversation is not { Kind: ConversationKind.Interactive }) return new(ConversationInputStartStatus.Idle);
            claimed = await _scheduler.TryClaimNextFollowUpAsync(conversationId, handle.RunId, Guid.NewGuid(), handle.CancellationToken).ConfigureAwait(false);
            if (claimed is null) return new(ConversationInputStartStatus.Blocked);
            var snapshot = claimed.ExecutionSnapshot ?? throw new InvalidOperationException(ConversationInputReason.AgentChanged);
            var prepared = await _validator.PrepareAsync(conversation, snapshot, handle.CancellationToken).ConfigureAwait(false);
            var claimId = claimed.ClaimId ?? throw new InvalidOperationException("Missing durable claim identity.");
            var claim = new ConversationInputClaim(claimed.Id, conversationId, claimed.Revision, claimId,
                handle.RunId, ConversationInputMessageId.Compute(claimId, claimed.Id));
            var request = new DesktopConversationTurnRequest(conversation, prepared.Agent, claimed.Prompt,
                snapshot.ModelProfileId, prepared.WorkspaceRoot, snapshot.ToolPermissionMode, conversationId, claim);
            lock (_taskGate)
            {
                _shutdown.Token.ThrowIfCancellationRequested();
                var task = Task.Run(() => RunAsync(handle, request), CancellationToken.None);
                _running.Add(task);
                _ = task.ContinueWith(ObserveCompletion, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
            }
            handedOff = true;
            _changes.Notify(conversationId);
            return new(ConversationInputStartStatus.Started, claimed.Id);
        }
        catch (OperationCanceledException)
        {
            if (claimed is not null) await HoldAsync(claimed, ConversationInputReason.TurnCancelled).ConfigureAwait(false);
            throw;
        }
        catch (Exception exception)
        {
            if (claimed is null) throw;
            var reason = exception.Message is ConversationInputReason.AgentMissing or ConversationInputReason.AgentChanged
                or ConversationInputReason.ModelDisabled or ConversationInputReason.WorkspaceMissing
                ? exception.Message : ConversationInputReason.ExecutionStartFailed;
            await HoldAsync(claimed, reason).ConfigureAwait(false);
            return new(ConversationInputStartStatus.Failed, claimed.Id, reason);
        }
        finally { if (!handedOff) _runs.Complete(handle, false); }
    }

    private bool IsDispatchable(ConversationInputQueueState state, Guid? immediateInputId)
    {
        if (state.Paused || state.Items.Count == 0) return false;
        if (_featureSwitch.QueueEnabled)
            return state.Items[0] is { Kind: ConversationInputKind.FollowUp, Status: ConversationInputStatus.Pending };
        // The disabled switch may still run the one input it just accepted, and nothing else.
        return immediateInputId is { } id && state.Items.Count == 1 && state.Items[0].InputId == id;
    }

    private async Task HoldAsync(ConversationInputRecord input, string reason)
    {
        await _store.HoldAsync(input.Id, input.Revision, reason, null, CancellationToken.None).ConfigureAwait(false);
        await PauseQueueAsync(input.ConversationId, reason).ConfigureAwait(false);
    }

    internal async Task PauseQueueAsync(Guid conversationId, string reason)
    {
        await _scheduler.PauseQueueAsync(conversationId, reason, CancellationToken.None).ConfigureAwait(false);
        _changes.Notify(conversationId);
    }

    internal async Task ValidateRetryAsync(Guid conversationId, ConversationInputExecutionSnapshot snapshot, CancellationToken token)
    {
        var conversation = await _conversations.GetConversationAsync(conversationId, token).ConfigureAwait(false)
            ?? throw new InvalidOperationException("conversation-unavailable");
        await _validator.PrepareAsync(conversation, snapshot, token).ConfigureAwait(false);
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
                if (!succeeded) await PauseBeforeReleaseAsync(handle.ConversationId, MapFailureReason(result)).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                var reason = handle.ExecutionResult?.Persisted == true
                    ? ConversationInputReason.TurnCancelled : ConversationInputReason.PersistFailed;
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
        if (succeeded && _featureSwitch.QueueEnabled && !_shutdown.IsCancellationRequested)
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
                await Task.Delay(ScanInterval, _timeProvider, _shutdown.Token).ConfigureAwait(false);
            }
        }
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
}
