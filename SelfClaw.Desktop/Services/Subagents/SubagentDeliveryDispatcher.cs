using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Desktop.Services.Runtime;

namespace SelfClaw.Desktop.Services.Subagents;

internal sealed class SubagentDeliveryDispatcher : BackgroundService
{
    private const int MaximumConcurrentContinuations = 4;
    private static readonly TimeSpan ScanInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan CoalescingWindow = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromSeconds(45);

    private readonly ISubagentDeliveryStore _deliveryStore;
    private readonly IConversationRepository _conversationRepository;
    private readonly ConversationTurnEngine _turnEngine;
    private readonly SubagentContinuationExecutor _executor;
    private readonly DesktopNotificationService _notificationService;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<SubagentDeliveryDispatcher> _logger;
    private readonly HashSet<Task> _runningContinuations = [];

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
            await RecoverExpiredLeasesAsync(stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                RemoveCompletedContinuations();
                var claimed = false;
                while (_runningContinuations.Count < MaximumConcurrentContinuations &&
                       await TryStartContinuationAsync(stoppingToken))
                {
                    claimed = true;
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

    private async Task<bool> TryStartContinuationAsync(CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        var mailbox = await _deliveryStore.PeekReadyMailboxAsync(
            now,
            now - CoalescingWindow,
            cancellationToken);
        if (mailbox is null)
        {
            return false;
        }

        var parent = await _conversationRepository.GetConversationAsync(
            mailbox.ParentConversationId,
            cancellationToken);
        if (parent is null || parent.Kind != ConversationKind.Interactive)
        {
            return false;
        }

        var runtimeState = await _turnEngine.TryAdmitContinuationAsync(parent, cancellationToken);
        if (runtimeState is null)
        {
            return false;
        }

        var lease = await _deliveryStore.TryLeaseBatchAsync(
            mailbox,
            Guid.NewGuid(),
            Guid.NewGuid(),
            now,
            now + LeaseDuration,
            SubagentCompletionBatchSerializer.MaximumBatchBytes,
            cancellationToken);
        if (lease is null)
        {
            await _turnEngine.CompleteContinuationAsync(
                runtimeState,
                publishPersistedTurn: false,
                CancellationToken.None);
            return true;
        }

        _logger.LogInformation(
            "Subagent continuation leased. ParentConversationId={ParentConversationId} ParentTurnId={ParentTurnId} ContinuationTurnId={ContinuationTurnId} DeliveryCount={DeliveryCount} Attempt={Attempt}",
            lease.ParentConversationId,
            lease.ParentTurnId,
            lease.ContinuationTurnId,
            lease.Deliveries.Count,
            lease.Deliveries.Max(delivery => delivery.AttemptCount));
        _runningContinuations.Add(RunContinuationAsync(parent, runtimeState, lease, cancellationToken));
        return true;
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
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "Subagent continuation worker escaped its terminal handling. ParentConversationId={ParentConversationId} ContinuationTurnId={ContinuationTurnId}",
                lease.ParentConversationId,
                lease.ContinuationTurnId);
            try
            {
                await _turnEngine.CompleteContinuationAsync(
                    runtimeState,
                    publishPersistedTurn: false,
                    CancellationToken.None);
            }
            catch (Exception completionException)
            {
                _logger.LogError(
                    completionException,
                    "Failed to abandon the escaped Subagent continuation state. ParentConversationId={ParentConversationId}",
                    lease.ParentConversationId);
            }
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
