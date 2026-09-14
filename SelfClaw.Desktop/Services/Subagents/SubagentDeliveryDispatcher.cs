using SelfClaw.Desktop.Services.Notifications;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Desktop.Services.Runtime;

namespace SelfClaw.Desktop.Services.Subagents;

internal sealed class SubagentDeliveryDispatcher : BackgroundService
{
    private const int MaximumConcurrentContinuations = 4;
    private const int MaximumCandidatesPerScan = 32;
    private static readonly TimeSpan ScanInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan CoalescingWindow = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan RecoveryInterval = TimeSpan.FromSeconds(15);

    private readonly ISubagentDeliveryStore _deliveryStore;
    private readonly IConversationRepository _conversationRepository;
    private readonly ConversationTurnEngine _turnEngine;
    private readonly SubagentContinuationExecutor _executor;
    private readonly DesktopNotificationService _notificationService;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<SubagentDeliveryDispatcher> _logger;
    private readonly HashSet<Task> _runningContinuations = [];
    private readonly HashSet<Guid> _skippedParents = [];
    private DateTimeOffset _nextRecoveryAtUtc;

    public SubagentDeliveryDispatcher(
        ISubagentDeliveryStore deliveryStore,
        IConversationRepository conversationRepository,
        ConversationTurnEngine turnEngine,
        SubagentContinuationExecutor executor,
        DesktopNotificationService notificationService,
        ILogger<SubagentDeliveryDispatcher> logger)
        : this(
            deliveryStore,
            conversationRepository,
            turnEngine,
            executor,
            notificationService,
            TimeProvider.System,
            logger)
    {
    }

    internal SubagentDeliveryDispatcher(
        ISubagentDeliveryStore deliveryStore,
        IConversationRepository conversationRepository,
        ConversationTurnEngine turnEngine,
        SubagentContinuationExecutor executor,
        DesktopNotificationService notificationService,
        TimeProvider timeProvider,
        ILogger<SubagentDeliveryDispatcher> logger)
    {
        _deliveryStore = deliveryStore;
        _conversationRepository = conversationRepository;
        _turnEngine = turnEngine;
        _executor = executor;
        _notificationService = notificationService;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                RemoveCompletedContinuations();
                var claimed = false;
                try
                {
                    var now = _timeProvider.GetUtcNow();
                    if (now >= _nextRecoveryAtUtc)
                    {
                        await RecoverExpiredLeasesAsync(stoppingToken);
                        _nextRecoveryAtUtc = now + RecoveryInterval;
                    }
                    while (_runningContinuations.Count < MaximumConcurrentContinuations &&
                           await TryStartContinuationAsync(stoppingToken))
                    {
                        claimed = true;
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    _logger.LogError(exception, "Subagent delivery scan failed; the next scan will retry.");
                }

                if (!claimed)
                {
                    await Task.Delay(ScanInterval, _timeProvider, stoppingToken);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            await Task.WhenAll(_runningContinuations.ToArray());
        }
    }

    private async Task RecoverExpiredLeasesAsync(CancellationToken cancellationToken)
    {
        var deadLetters = await _deliveryStore.RecoverExpiredLeasesAsync(
            _timeProvider.GetUtcNow(),
            cancellationToken);
        foreach (var delivery in deadLetters)
        {
            var parent = await _conversationRepository.GetConversationAsync(
                delivery.ParentConversationId,
                cancellationToken);
            if (parent is not null)
            {
                NotifyDeadLetter(parent, "A previous Subagent continuation was interrupted after tool execution.");
            }
        }
    }

    internal async Task<bool> TryStartContinuationAsync(CancellationToken cancellationToken)
    {
        var excluded = _turnEngine.GetUnavailableContinuationParents().ToHashSet();
        excluded.UnionWith(_skippedParents);
        for (var candidate = 0; candidate < MaximumCandidatesPerScan; candidate++)
        {
            var now = _timeProvider.GetUtcNow();
            var mailbox = await _deliveryStore.PeekReadyMailboxAsync(now, now - CoalescingWindow, cancellationToken, excluded);
            if (mailbox is null)
            {
                _skippedParents.Clear();
                return false;
            }
            excluded.Add(mailbox.ParentConversationId);
            _skippedParents.Add(mailbox.ParentConversationId);
            try
            {
                var parent = await _conversationRepository.GetConversationAsync(mailbox.ParentConversationId, cancellationToken);
                if (parent is not { Kind: ConversationKind.Interactive }) continue;
                var runtimeState = await _turnEngine.TryAdmitContinuationAsync(parent, cancellationToken);
                if (runtimeState is null) continue;
                if (await StartAdmittedContinuationAsync(parent, runtimeState, mailbox, cancellationToken))
                {
                    _skippedParents.Remove(parent.Id);
                    return true;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Failed to start a continuation for parent {ParentConversationId}; scanning other parents.",
                    mailbox.ParentConversationId);
            }
        }

        return false;
    }

    private async Task<bool> StartAdmittedContinuationAsync(ConversationRecord parent, ConversationRuntimeState runtimeState,
        SubagentMailboxKey mailbox, CancellationToken cancellationToken)
    {
        SubagentDeliveryLease? lease = null;
        var handedOff = false;
        try
        {
            var now = _timeProvider.GetUtcNow();
            lease = await _deliveryStore.TryLeaseBatchAsync(
                mailbox,
                Guid.NewGuid(),
                Guid.NewGuid(),
                now,
                now + LeaseDuration,
                SubagentCompletionBatchSerializer.MaximumBatchBytes,
                cancellationToken);
            if (lease is null)
            {
                return false;
            }

            cancellationToken.ThrowIfCancellationRequested();
            _logger.LogInformation(
                "Subagent continuation leased. ParentConversationId={ParentConversationId} ParentTurnId={ParentTurnId} ContinuationTurnId={ContinuationTurnId} DeliveryCount={DeliveryCount} Attempt={Attempt}",
                lease.ParentConversationId,
                lease.ParentTurnId,
                lease.ContinuationTurnId,
                lease.Deliveries.Count,
                lease.Deliveries.Max(delivery => delivery.AttemptCount));
            _runningContinuations.Add(RunContinuationAsync(parent, runtimeState, lease, cancellationToken));
            handedOff = true;
            return true;
        }
        finally
        {
            if (!handedOff)
            {
                try
                {
                    if (lease is not null)
                    {
                        await _deliveryStore.TryResolveAsync(lease,
                            new SubagentDeliveryResolution(SubagentDeliveryResolutionKind.RetryableFailure,
                                null, "Continuation execution was not started.", _timeProvider.GetUtcNow()),
                            CancellationToken.None);
                    }
                }
                finally
                {
                    await _turnEngine.CompleteContinuationAsync(runtimeState, false, CancellationToken.None);
                }
            }
        }
    }

    private async Task RunContinuationAsync(
        ConversationRecord parent,
        ConversationRuntimeState runtimeState,
        SubagentDeliveryLease lease,
        CancellationToken cancellationToken)
    {
        try
        {
            await _executor.ExecuteAsync(parent, runtimeState, lease, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "Subagent continuation worker escaped its terminal handling. ParentConversationId={ParentConversationId} ContinuationTurnId={ContinuationTurnId}",
                lease.ParentConversationId,
                lease.ContinuationTurnId);
        }
    }

    private void NotifyDeadLetter(ConversationRecord parent, string message)
        => _notificationService.ShowSubagentContinuationFailed(
            parent.Id,
            parent.Title,
            message);

    private void RemoveCompletedContinuations()
        => _runningContinuations.RemoveWhere(task => task.IsCompleted);
}
