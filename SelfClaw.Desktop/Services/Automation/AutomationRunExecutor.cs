using System.IO;
using Microsoft.Extensions.Logging;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Desktop.Services.Runtime;

namespace SelfClaw.Desktop.Services.Automation;

/// <summary>
/// Runs one automation run as a headless Direct turn. It resolves the model, capability snapshot and
/// workspace per attempt, records them on the run, streams the turn through the interactive transcript
/// writer, then collapses the turn outcome into a run terminal state.
/// </summary>
internal sealed class AutomationRunExecutor : IAutomationRunExecutor
{
    private const int MaximumAttempts = 3;
    private const int MaximumErrorMessageLength = 2048;
    private const int CapabilitySnapshotVersion = 1;

    private static readonly TimeSpan[] RetryBackoffs = [TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30)];

    /// <summary>
    /// Failures whose cause is configuration, not transport. They can never succeed on a retry, so the run
    /// ends immediately. Anything else with no tool evidence is treated as a transient provider failure.
    /// </summary>
    private static readonly string[] ConfigurationFailureSignatures =
    [
        "Required Plugin",
        "Required Skill",
        "Required MCP",
        "tool name collision",
        "not installed",
        "is disabled",
        "not bound",
        "invalid",
        "context window",
        "provider request limit",
        "filtered or refused",
        "output-token limit"
    ];

    private readonly IConversationRepository _conversations;
    private readonly IConversationTurnRepository _turns;
    private readonly IAutomationRunStore _runStore;
    private readonly IAutomationRunAttemptStore _attemptStore;
    private readonly IWorkspaceRootRepository _workspaces;
    private readonly IAiModelCatalog _models;
    private readonly IAgentChatRuntime _chatRuntime;
    private readonly ConversationTurnRecorder _recorder;
    private readonly DesktopTurnFinalizer _finalizer;
    private readonly AutomationRunSnapshotSerializer _snapshots;
    private readonly AutomationToolApprovalHandler _approval;
    private readonly AutomationRunCancellationRegistry _cancellations;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<AutomationRunExecutor> _logger;

    public AutomationRunExecutor(
        IConversationRepository conversations,
        IConversationTurnRepository turns,
        IAutomationRunStore runStore,
        IAutomationRunAttemptStore attemptStore,
        IWorkspaceRootRepository workspaces,
        IAiModelCatalog models,
        IAgentChatRuntime chatRuntime,
        ConversationTurnRecorder recorder,
        DesktopTurnFinalizer finalizer,
        AutomationRunSnapshotSerializer snapshots,
        AutomationToolApprovalHandler approval,
        AutomationRunCancellationRegistry cancellations,
        ILogger<AutomationRunExecutor> logger)
        : this(
            conversations,
            turns,
            runStore,
            attemptStore,
            workspaces,
            models,
            chatRuntime,
            recorder,
            finalizer,
            snapshots,
            approval,
            cancellations,
            TimeProvider.System,
            logger)
    {
    }

    internal AutomationRunExecutor(
        IConversationRepository conversations,
        IConversationTurnRepository turns,
        IAutomationRunStore runStore,
        IAutomationRunAttemptStore attemptStore,
        IWorkspaceRootRepository workspaces,
        IAiModelCatalog models,
        IAgentChatRuntime chatRuntime,
        ConversationTurnRecorder recorder,
        DesktopTurnFinalizer finalizer,
        AutomationRunSnapshotSerializer snapshots,
        AutomationToolApprovalHandler approval,
        AutomationRunCancellationRegistry cancellations,
        TimeProvider timeProvider,
        ILogger<AutomationRunExecutor> logger)
    {
        _conversations = conversations;
        _turns = turns;
        _runStore = runStore;
        _attemptStore = attemptStore;
        _workspaces = workspaces;
        _models = models;
        _chatRuntime = chatRuntime;
        _recorder = recorder;
        _finalizer = finalizer;
        _snapshots = snapshots;
        _approval = approval;
        _cancellations = cancellations;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task ExecuteAsync(AutomationRunRecord run, AutomationTaskRecord task, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(task);
        using var timeout = new CancellationTokenSource(AutomationDefaults.MaxRunSeconds, _timeProvider);
        using var execution = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            var terminal = await ExecuteAttemptsAsync(run, task, timeout, execution.Token).ConfigureAwait(false);
            await _runStore.TryCompleteAsync(run.Id, terminal, CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (execution.IsCancellationRequested)
        {
            // The attempt already finalized its turn; the run row keeps the facts recorded at its start.
            var current = await _runStore.GetAsync(run.Id, CancellationToken.None).ConfigureAwait(false) ?? run;
            await _runStore.TryCompleteAsync(run.Id, CreateCancellationTerminal(current, timeout), CancellationToken.None)
                .ConfigureAwait(false);
            throw;
        }
    }

    private async Task<AutomationRunRecord> ExecuteAttemptsAsync(
        AutomationRunRecord run,
        AutomationTaskRecord task,
        CancellationTokenSource timeout,
        CancellationToken cancellationToken)
    {
        var conversationId = Guid.NewGuid();
        for (var attempt = 1; ; attempt++)
        {
            var result = await RunAttemptAsync(run, task, conversationId, attempt, timeout, cancellationToken)
                .ConfigureAwait(false);
            if (!result.Retryable || attempt >= MaximumAttempts)
            {
                return result.Terminal;
            }

            await Task.Delay(RetryBackoffs[attempt - 1], _timeProvider, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<AutomationAttemptResult> RunAttemptAsync(
        AutomationRunRecord run,
        AutomationTaskRecord task,
        Guid conversationId,
        int attempt,
        CancellationTokenSource timeout,
        CancellationToken cancellationToken)
    {
        AutomationAttemptResolution resolution;
        try
        {
            resolution = await ResolveAttemptAsync(task, cancellationToken).ConfigureAwait(false);
        }
        catch (AutomationExecutionException exception)
        {
            return new AutomationAttemptResult(
                CreateTerminal(run, AutomationRunStatus.Failed, exception.ErrorCode, exception.Message, null, attempt, null),
                Retryable: false);
        }

        var started = await StartTurnAsync(run.Id, conversationId, task, resolution, attempt, cancellationToken)
            .ConfigureAwait(false);
        var conversation = started.Conversation;
        var turnRecord = started.Turn;
        var state = await LoadStateAsync(conversation, cancellationToken).ConfigureAwait(false);
        var agentTurn = new AgentTurnState(turnRecord, resolution.Agent);
        _recorder.BeginTurn(state, agentTurn);
        try
        {
            var request = CreateRequest(conversation, turnRecord, resolution, state);
            await foreach (var streamEvent in _chatRuntime.StreamTurnAsync(request, cancellationToken).ConfigureAwait(false))
            {
                await _recorder.ApplyEventAsync(state, agentTurn, streamEvent, _finalizer, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            await FinalizeTurnAsync(state, agentTurn, CancellationKind(timeout), CancellationMessage(timeout))
                .ConfigureAwait(false);
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogError(exception, "Automation run {RunId} attempt {Attempt} failed.", run.Id, attempt);
            await FinalizeTurnAsync(state, agentTurn, TurnFinalizationKind.Failed, exception.Message).ConfigureAwait(false);
            return new AutomationAttemptResult(
                CreateTerminal(run, AutomationRunStatus.Failed, AutomationErrorCodes.ProviderFailed, exception.Message,
                    conversation.Id, attempt, resolution),
                Retryable: agentTurn.ToolRunsByCallId.Count == 0 && IsRetryableFailure(exception.Message));
        }

        if (!agentTurn.Completed)
        {
            await FinalizeTurnAsync(state, agentTurn, TurnFinalizationKind.Failed,
                "The automation stream ended without a terminal event.").ConfigureAwait(false);
        }

        var finalTurn = state.Turns.Single(item => item.Id == turnRecord.Id);
        var (status, errorCode, retryable) = ClassifyTurnOutcome(
            finalTurn.Status, finalTurn.ErrorMessage, agentTurn.ToolRunsByCallId.Count);
        return new AutomationAttemptResult(
            CreateTerminal(run, status, errorCode, finalTurn.ErrorMessage, conversation.Id, attempt, resolution),
            retryable);
    }

    private async Task<AutomationAttemptResolution> ResolveAttemptAsync(
        AutomationTaskRecord task,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(task.Prompt))
        {
            throw new AutomationExecutionException(
                AutomationErrorCodes.TriggerRequiresPrompt,
                "An automation run requires a prompt.");
        }

        var modelProfileId = task.ModelProfileId
            ?? await _models.GetDefaultModelAsync(AiModelSelectionScopes.DesktopDefault, cancellationToken).ConfigureAwait(false)
            ?? throw new AutomationExecutionException(
                AutomationErrorCodes.ModelUnavailable,
                "No default model is configured for automation runs.");
        if (!await _models.IsModelAvailableAsync(modelProfileId, cancellationToken).ConfigureAwait(false))
        {
            throw new AutomationExecutionException(
                AutomationErrorCodes.ModelUnavailable,
                $"Automation model '{modelProfileId}' is disabled or was deleted.");
        }

        var workspaceRoot = await ResolveWorkspaceAsync(task.WorkspaceRootId, cancellationToken).ConfigureAwait(false);
        var snapshot = new AutomationCapabilitySnapshot(
            CapabilitySnapshotVersion,
            task.ToolPolicy,
            task.PluginIds,
            task.SkillIds,
            task.McpServerIds,
            task.Instructions);
        var agent = new AgentRuntimeDefinition(
            $"automation:{task.Id:D}",
            task.Title,
            "Scheduled automation task",
            AgentExecutionMode.Direct,
            task.ToolPolicy,
            task.PluginIds,
            task.SkillIds,
            task.McpServerIds,
            SubagentIds: [],
            task.Instructions);
        return new AutomationAttemptResolution(modelProfileId, agent, workspaceRoot, _snapshots.Serialize(snapshot));
    }

    private async Task<WorkspaceRoot?> ResolveWorkspaceAsync(Guid? workspaceRootId, CancellationToken cancellationToken)
    {
        if (workspaceRootId is not Guid rootId)
        {
            return null;
        }

        var root = (await _workspaces.ListWorkspaceRootsAsync(cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(item => item.Id == rootId);
        if (root is null)
        {
            throw new AutomationExecutionException(
                AutomationErrorCodes.WorkspaceUnavailable,
                $"Automation workspace '{rootId}' no longer exists.");
        }

        if (!Directory.Exists(root.RootPath))
        {
            throw new AutomationExecutionException(
                AutomationErrorCodes.WorkspaceUnavailable,
                $"Automation workspace '{root.Name}' is not available at '{root.RootPath}'.");
        }

        return root;
    }

    private async Task<AutomationTurnStart> StartTurnAsync(
        Guid runId,
        Guid conversationId,
        AutomationTaskRecord task,
        AutomationAttemptResolution resolution,
        int attempt,
        CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        var conversation = new ConversationRecord(
            conversationId,
            task.Title,
            resolution.WorkspaceRoot?.Id,
            ConversationMode.Programming,
            ToolPermissionMode.FullAccess,
            resolution.Agent.Id,
            now,
            now,
            Kind: ConversationKind.Automation);
        var turn = new ConversationTurnRecord(
            Guid.NewGuid(),
            conversationId,
            AgentExecutionMode.Direct,
            DirectTurnOrigin.Automation,
            ConversationTurnStatus.Running,
            now);
        await _turns.StartTurnAsync(
            new ConversationTurnStart(conversation, turn, task.Prompt!, Guid.NewGuid()),
            cancellationToken).ConfigureAwait(false);
        await _attemptStore.RecordAttemptAsync(
            new AutomationRunAttempt(
                runId,
                attempt,
                conversationId,
                resolution.ModelProfileId,
                resolution.CapabilitySnapshotJson,
                resolution.WorkspaceRoot?.Id,
                resolution.WorkspaceRoot?.RootPath,
                now),
            cancellationToken).ConfigureAwait(false);
        return new AutomationTurnStart(conversation, turn);
    }

    private async Task<ConversationRuntimeState> LoadStateAsync(
        ConversationRecord conversation,
        CancellationToken cancellationToken)
    {
        var turns = await _turns.ListTurnsAsync(conversation.Id, cancellationToken).ConfigureAwait(false);
        var messages = await _conversations.ListMessagesAsync(conversation.Id, cancellationToken).ConfigureAwait(false);
        var toolRuns = await _conversations.ListToolExecutionsAsync(conversation.Id, cancellationToken).ConfigureAwait(false);
        return new ConversationRuntimeState(conversation, turns, messages, toolRuns);
    }

    private DirectChatTurnRequest CreateRequest(
        ConversationRecord conversation,
        ConversationTurnRecord turn,
        AutomationAttemptResolution resolution,
        ConversationRuntimeState state)
        => new(
            turn.Id,
            conversation.Id,
            resolution.WorkspaceRoot,
            resolution.Agent,
            state.Messages,
            state.Turns,
            resolution.ModelProfileId,
            ToolPermissionMode.FullAccess,
            _approval,
            new DirectTurnExecutionContext(DirectTurnOrigin.Automation, CapabilityCeiling: null, CompletionBatch: null),
            state.ToolRuns);

    private Task FinalizeTurnAsync(
        ConversationRuntimeState state,
        AgentTurnState turn,
        TurnFinalizationKind kind,
        string message)
        => turn.Completed || turn.PendingFinalization is not null
            ? Task.CompletedTask
            : _recorder.FinalizeInterruptedAsync(state, turn, kind, message, _finalizer);

    private TurnFinalizationKind CancellationKind(CancellationTokenSource timeout)
        => timeout.IsCancellationRequested ? TurnFinalizationKind.Failed : TurnFinalizationKind.Cancelled;

    private string CancellationMessage(CancellationTokenSource timeout)
        => timeout.IsCancellationRequested
            ? $"The automation run exceeded its {AutomationDefaults.MaxRunSeconds.TotalMinutes} minute time limit."
            : "The automation run was cancelled.";

    private AutomationRunRecord CreateCancellationTerminal(AutomationRunRecord run, CancellationTokenSource timeout)
    {
        var (status, errorCode, message) = timeout.IsCancellationRequested
            ? (AutomationRunStatus.Failed, AutomationErrorCodes.TimedOut,
                $"The automation run exceeded its {AutomationDefaults.MaxRunSeconds.TotalMinutes} minute time limit.")
            : _cancellations.IsUserCancellationRequested(run.Id)
                ? (AutomationRunStatus.Cancelled, AutomationErrorCodes.CancelledByUser,
                    "The automation run was stopped by the user.")
                : (AutomationRunStatus.Cancelled, AutomationErrorCodes.ApplicationStopping,
                    "The application stopped while the automation run was executing.");
        return run with
        {
            Status = status,
            ErrorCode = errorCode,
            ErrorMessage = message,
            CompletedAtUtc = _timeProvider.GetUtcNow()
        };
    }

    private static (AutomationRunStatus Status, string? ErrorCode, bool Retryable) ClassifyTurnOutcome(
        ConversationTurnStatus turnStatus,
        string? errorMessage,
        int toolExecutionCount)
        => turnStatus switch
        {
            ConversationTurnStatus.Succeeded => (AutomationRunStatus.Completed, null, false),
            ConversationTurnStatus.Failed when toolExecutionCount == 0 && IsRetryableFailure(errorMessage) =>
                (AutomationRunStatus.Failed, AutomationErrorCodes.ProviderFailed, true),
            ConversationTurnStatus.Failed =>
                (AutomationRunStatus.Failed, AutomationErrorCodes.ProviderFailed, false),
            ConversationTurnStatus.Cancelled =>
                (AutomationRunStatus.Cancelled, AutomationErrorCodes.CancelledByUser, false),
            ConversationTurnStatus.Interrupted =>
                (AutomationRunStatus.Interrupted, AutomationErrorCodes.ProcessInterrupted, false),
            _ => (AutomationRunStatus.Failed, AutomationErrorCodes.ProviderFailed, false)
        };

    private static bool IsRetryableFailure(string? errorMessage)
        => string.IsNullOrWhiteSpace(errorMessage) ||
           !ConfigurationFailureSignatures.Any(signature =>
               errorMessage.Contains(signature, StringComparison.OrdinalIgnoreCase));

    private AutomationRunRecord CreateTerminal(
        AutomationRunRecord run,
        AutomationRunStatus status,
        string? errorCode,
        string? errorMessage,
        Guid? conversationId,
        int attempt,
        AutomationAttemptResolution? resolution)
        => run with
        {
            ConversationId = conversationId ?? run.ConversationId,
            Status = status,
            SkipReason = null,
            Attempt = attempt,
            ModelProfileId = resolution?.ModelProfileId ?? run.ModelProfileId,
            CapabilitySnapshotJson = resolution?.CapabilitySnapshotJson ?? run.CapabilitySnapshotJson,
            WorkspaceRootId = resolution?.WorkspaceRoot?.Id ?? run.WorkspaceRootId,
            WorkspacePathSnapshot = resolution?.WorkspaceRoot?.RootPath ?? run.WorkspacePathSnapshot,
            CompletedAtUtc = _timeProvider.GetUtcNow(),
            ErrorCode = errorCode,
            ErrorMessage = errorMessage is { Length: > MaximumErrorMessageLength }
                ? errorMessage[..MaximumErrorMessageLength]
                : errorMessage
        };

    private sealed record AutomationTurnStart(ConversationRecord Conversation, ConversationTurnRecord Turn);

    private sealed record AutomationAttemptResolution(
        Guid ModelProfileId,
        AgentRuntimeDefinition Agent,
        WorkspaceRoot? WorkspaceRoot,
        string CapabilitySnapshotJson);

    private sealed record AutomationAttemptResult(AutomationRunRecord Terminal, bool Retryable);

    private sealed class AutomationExecutionException(string errorCode, string message) : Exception(message)
    {
        internal string ErrorCode { get; } = errorCode;
    }
}
