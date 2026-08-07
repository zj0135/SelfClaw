using System.IO;
using Microsoft.Extensions.Logging;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Core.Runtime.Agent;
using SelfClaw.Desktop.Services.Runtime;
using SelfClaw.Desktop.Services.Subagents.Models;
using SelfClaw.Infrastructure.Tools.Transcript.Models;

namespace SelfClaw.Desktop.Services.Subagents;

internal sealed class SubagentTaskExecutor
{
    private readonly IConversationRepository _conversationRepository;
    private readonly ISubagentTaskExecutionStore _taskStore;
    private readonly IAgentChatRuntime _chatRuntime;
    private readonly ConversationTurnRecorder _turnRecorder;
    private readonly DesktopToolApprovalHandler _approvalHandler;
    private readonly SubagentTaskSnapshotSerializer _snapshotSerializer;
    private readonly SubagentTaskPreflight _preflight;
    private readonly SubagentTaskExecutionRegistry _executionRegistry;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<SubagentTaskExecutor> _logger;

    public SubagentTaskExecutor(
        IConversationRepository conversationRepository,
        ISubagentTaskExecutionStore taskStore,
        IAgentChatRuntime chatRuntime,
        ConversationTurnRecorder turnRecorder,
        DesktopToolApprovalHandler approvalHandler,
        SubagentTaskSnapshotSerializer snapshotSerializer,
        SubagentTaskPreflight preflight,
        SubagentTaskExecutionRegistry executionRegistry,
        ILogger<SubagentTaskExecutor> logger)
        : this(
            conversationRepository,
            taskStore,
            chatRuntime,
            turnRecorder,
            approvalHandler,
            snapshotSerializer,
            preflight,
            executionRegistry,
            TimeProvider.System,
            logger)
    {
    }

    internal SubagentTaskExecutor(
        IConversationRepository conversationRepository,
        ISubagentTaskExecutionStore taskStore,
        IAgentChatRuntime chatRuntime,
        ConversationTurnRecorder turnRecorder,
        DesktopToolApprovalHandler approvalHandler,
        SubagentTaskSnapshotSerializer snapshotSerializer,
        SubagentTaskPreflight preflight,
        SubagentTaskExecutionRegistry executionRegistry,
        TimeProvider timeProvider,
        ILogger<SubagentTaskExecutor> logger)
    {
        _conversationRepository = conversationRepository;
        _taskStore = taskStore;
        _chatRuntime = chatRuntime;
        _turnRecorder = turnRecorder;
        _approvalHandler = approvalHandler;
        _snapshotSerializer = snapshotSerializer;
        _preflight = preflight;
        _executionRegistry = executionRegistry;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    internal async Task ExecuteAsync(SubagentTaskRecord task, CancellationToken hostCancellationToken)
    {
        ArgumentNullException.ThrowIfNull(task);
        using var timeout = new CancellationTokenSource(
            TimeSpan.FromSeconds(task.MaxRunSeconds),
            _timeProvider);
        using var execution = CancellationTokenSource.CreateLinkedTokenSource(
            hostCancellationToken,
            timeout.Token);
        _executionRegistry.Register(task.Id, execution);
        if (task.CancelRequestedAtUtc is not null)
        {
            _executionRegistry.RequestCancellation(task.Id);
        }

        ChildRuntime? runtime = null;
        AgentTurnState? turn = null;
        var committer = new SubagentChildTurnCommitter(_taskStore, task.Id, _timeProvider);
        try
        {
            runtime = await LoadRuntimeAsync(task, execution.Token);
            turn = CreateTurnState(task, runtime.State);
            _turnRecorder.BeginTurn(runtime.State, turn);
            var request = await CreateRequestAsync(task, runtime.Messages, execution.Token);
            await foreach (var streamEvent in _chatRuntime.StreamTurnAsync(request, execution.Token))
            {
                await _turnRecorder.ApplyEventAsync(
                    runtime.State,
                    turn,
                    streamEvent,
                    committer,
                    execution.Token);
            }

            if (!turn.Completed)
            {
                committer.OverrideTerminal(
                    SubagentTaskStatus.Failed,
                    SubagentErrorCodes.ProviderFailed,
                    "The Subagent stream ended without a terminal event.");
                await _turnRecorder.FinalizeInterruptedAsync(
                    runtime.State,
                    turn,
                    TurnFinalizationKind.Failed,
                    "The Subagent stream ended without a terminal event.",
                    committer);
            }
        }
        catch (OperationCanceledException)
        {
            (runtime, turn) = EnsureRuntime(task, runtime, turn);
            await FinalizeCancellationAsync(
                task,
                runtime.State,
                turn,
                committer,
                timeout.IsCancellationRequested,
                hostCancellationToken.IsCancellationRequested);
        }
        catch (SubagentExecutionPreflightException exception)
        {
            (runtime, turn) = EnsureRuntime(task, runtime, turn);
            await FinalizeFailureAsync(
                runtime.State,
                turn,
                committer,
                exception.ErrorCode,
                exception.Message);
        }
        catch (InvalidDataException exception)
        {
            (runtime, turn) = EnsureRuntime(task, runtime, turn);
            await FinalizeFailureAsync(
                runtime.State,
                turn,
                committer,
                SubagentErrorCodes.SnapshotInvalid,
                exception.Message);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Subagent task {TaskId} execution failed.", task.Id);
            (runtime, turn) = EnsureRuntime(task, runtime, turn);
            await FinalizeFailureAsync(
                runtime.State,
                turn,
                committer,
                SubagentErrorCodes.ProviderFailed,
                exception.Message);
        }
        finally
        {
            _executionRegistry.Unregister(task.Id);
            runtime?.State.Dispose();
        }
    }

    internal async Task RecoverInterruptedAsync(
        SubagentTaskRecord task,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(task);
        ChildRuntime runtime;
        try
        {
            runtime = await LoadRuntimeAsync(task, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "Subagent task {TaskId} recovery could not load its child transcript; a minimal terminal transcript will be used.",
                task.Id);
            runtime = CreateFallbackRuntime(task);
        }

        using (runtime.State)
        {
            var turn = CreateTurnState(task, runtime.State);
            var committer = new SubagentChildTurnCommitter(_taskStore, task.Id, _timeProvider);
            committer.OverrideTerminal(
                SubagentTaskStatus.Interrupted,
                SubagentErrorCodes.ProcessInterrupted,
                "The application stopped before the Subagent task reached a terminal state.");
            _turnRecorder.BeginTurn(runtime.State, turn);
            await _turnRecorder.FinalizeInterruptedAsync(
                runtime.State,
                turn,
                TurnFinalizationKind.Failed,
                "The application stopped before the Subagent task reached a terminal state.",
                committer);
        }
    }

    private (ChildRuntime Runtime, AgentTurnState Turn) EnsureRuntime(
        SubagentTaskRecord task,
        ChildRuntime? runtime,
        AgentTurnState? turn)
    {
        runtime ??= CreateFallbackRuntime(task);
        turn ??= CreateTurnState(task, runtime.State);
        _turnRecorder.BeginTurn(runtime.State, turn);
        return (runtime, turn);
    }

    private async Task<DirectChatTurnRequest> CreateRequestAsync(
        SubagentTaskRecord task,
        IReadOnlyList<MessageRecord> messages,
        CancellationToken cancellationToken)
    {
        var definition = _snapshotSerializer.DeserializeDefinition(task.DefinitionSnapshotJson);
        var parent = _snapshotSerializer.DeserializeParent(task.ParentExecutionSnapshotJson);
        ValidateSnapshots(task, definition, parent, messages);
        var resolvedModelProfileId = task.ResolvedModelProfileId
            ?? throw new InvalidDataException("The Subagent task has no resolved model snapshot.");
        var preflightRequest = new SubagentTaskStartRequest(
            task.ParentConversationId,
            task.ParentTurnId,
            task.SubagentId,
            task.TaskText,
            parent.Agent,
            parent.ModelProfileId,
            parent.WorkspaceRoot,
            parent.ToolPermissionMode,
            parent.CapabilityCeiling);
        var failure = await _preflight.CheckAsync(
            definition,
            preflightRequest,
            resolvedModelProfileId,
            cancellationToken);
        if (failure is not null)
        {
            throw new SubagentExecutionPreflightException(failure.ErrorCode, failure.ErrorMessage);
        }

        var agent = new AgentRuntimeDefinition(
            definition.Id,
            definition.Name,
            definition.Description,
            AgentExecutionMode.Direct,
            definition.ToolPolicy,
            definition.PluginIds,
            definition.SkillIds,
            definition.McpServerIds,
            SubagentIds: [],
            definition.Instructions);
        return new DirectChatTurnRequest(
            task.ChildTurnId,
            task.ChildConversationId,
            parent.WorkspaceRoot,
            agent,
            messages,
            resolvedModelProfileId,
            parent.ToolPermissionMode,
            _approvalHandler,
            new DirectTurnExecutionContext(
                DirectTurnOrigin.Subagent,
                parent.CapabilityCeiling,
                CompletionBatch: null));
    }

    private async Task<ChildRuntime> LoadRuntimeAsync(
        SubagentTaskRecord task,
        CancellationToken cancellationToken)
    {
        var conversation = await _conversationRepository.GetConversationAsync(
            task.ChildConversationId,
            cancellationToken)
            ?? throw new InvalidDataException("The Subagent child conversation is missing.");
        var messages = await _conversationRepository.ListMessagesAsync(
            task.ChildConversationId,
            cancellationToken);
        var toolExecutions = await _conversationRepository.ListToolExecutionsAsync(
            task.ChildConversationId,
            cancellationToken);
        var anchors = toolExecutions
            .Where(tool => tool.MessageId is not null && tool.AfterSegmentIndex is not null)
            .ToDictionary(
                tool => tool.Id,
                tool => new ToolRunAnchor(
                    tool.MessageId.GetValueOrDefault(),
                    tool.AfterSegmentIndex.GetValueOrDefault()));
        var providerMessages = messages.Where(message => message.Id != task.ChildTurnId).ToArray();
        return new ChildRuntime(
            new ConversationRuntimeState(conversation, messages, toolExecutions, anchors),
            providerMessages);
    }

    private ChildRuntime CreateFallbackRuntime(SubagentTaskRecord task)
    {
        var now = _timeProvider.GetUtcNow();
        var conversation = new ConversationRecord(
            task.ChildConversationId,
            $"Subagent: {task.SubagentName}",
            WorkspaceRootId: null,
            ConversationMode.Programming,
            ToolPermissionMode.RequireApproval,
            task.SubagentId,
            task.CreatedAtUtc,
            now,
            Kind: ConversationKind.Subagent,
            ParentConversationId: task.ParentConversationId);
        return new ChildRuntime(
            new ConversationRuntimeState(
                conversation,
                [],
                [],
                new Dictionary<Guid, ToolRunAnchor>()),
            []);
    }

    private static AgentTurnState CreateTurnState(
        SubagentTaskRecord task,
        ConversationRuntimeState runtimeState)
    {
        var agent = new AgentRuntimeDefinition(
            task.SubagentId,
            task.SubagentName,
            string.Empty,
            AgentExecutionMode.Direct,
            "read-only",
            [],
            [],
            [],
            [],
            string.Empty);
        var turn = new AgentTurnState(task.ChildTurnId, agent)
        {
            MessageCreated = runtimeState.Messages.Any(message => message.Id == task.ChildTurnId)
        };
        foreach (var tool in runtimeState.ToolRuns.Where(tool => tool.MessageId == task.ChildTurnId))
        {
            turn.ToolRunsByCallId[tool.CorrelationId ?? tool.Id.ToString("D")] = tool;
        }

        return turn;
    }

    private async Task FinalizeCancellationAsync(
        SubagentTaskRecord task,
        ConversationRuntimeState runtimeState,
        AgentTurnState turn,
        SubagentChildTurnCommitter committer,
        bool timedOut,
        bool applicationStopping)
    {
        SubagentTaskStatus status;
        TurnFinalizationKind kind;
        string errorCode;
        string message;
        if (_executionRegistry.IsCancellationRequested(task.Id))
        {
            status = SubagentTaskStatus.Cancelled;
            kind = TurnFinalizationKind.Cancelled;
            errorCode = SubagentErrorCodes.CancelledByParent;
            message = "The Subagent task was cancelled by its parent.";
        }
        else if (timedOut)
        {
            status = SubagentTaskStatus.Failed;
            kind = TurnFinalizationKind.Failed;
            errorCode = SubagentErrorCodes.TimedOut;
            message = $"The Subagent task exceeded its {task.MaxRunSeconds} second time limit.";
        }
        else if (applicationStopping)
        {
            status = SubagentTaskStatus.Cancelled;
            kind = TurnFinalizationKind.Cancelled;
            errorCode = SubagentErrorCodes.ApplicationStopping;
            message = "The application stopped while the Subagent task was running.";
        }
        else
        {
            status = SubagentTaskStatus.Failed;
            kind = TurnFinalizationKind.Failed;
            errorCode = SubagentErrorCodes.RuntimeCancelled;
            message = "The Subagent runtime cancelled without a parent cancellation request.";
        }

        committer.OverrideTerminal(status, errorCode, message);
        await _turnRecorder.FinalizeInterruptedAsync(runtimeState, turn, kind, message, committer);
    }

    private async Task FinalizeFailureAsync(
        ConversationRuntimeState runtimeState,
        AgentTurnState turn,
        SubagentChildTurnCommitter committer,
        string errorCode,
        string message)
    {
        committer.OverrideTerminal(SubagentTaskStatus.Failed, errorCode, message);
        await _turnRecorder.FinalizeInterruptedAsync(
            runtimeState,
            turn,
            TurnFinalizationKind.Failed,
            message,
            committer);
    }

    private static void ValidateSnapshots(
        SubagentTaskRecord task,
        SubagentDefinitionSnapshot definition,
        SubagentParentExecutionSnapshot parent,
        IReadOnlyList<MessageRecord> messages)
    {
        var valid = definition.Version == 1 &&
                    parent.Version == 1 &&
                    string.Equals(definition.Id, task.SubagentId, StringComparison.Ordinal) &&
                    task.ResolvedModelProfileId is not null &&
                    parent.ModelProfileId != Guid.Empty &&
                    messages.Count == 1 &&
                    messages[0].Role == MessageRole.User &&
                    messages[0].Status == MessageStatus.Completed &&
                    string.Equals(messages[0].MarkdownContent, task.TaskText, StringComparison.Ordinal);
        if (!valid)
        {
            throw new InvalidDataException("The Subagent execution snapshots or isolated child input are invalid.");
        }
    }

    private sealed record ChildRuntime(
        ConversationRuntimeState State,
        IReadOnlyList<MessageRecord> Messages);
}
