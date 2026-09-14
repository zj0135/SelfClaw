using SelfClaw.Desktop.Services.Tools;
using System.IO;
using Microsoft.Extensions.Logging;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Core.Runtime.Agent;
using SelfClaw.Desktop.Services.Runtime;
using SelfClaw.Desktop.Services.Subagents.Models;

namespace SelfClaw.Desktop.Services.Subagents;

internal sealed class SubagentTaskExecutor
{
    private readonly IConversationRepository _conversationRepository;
    private readonly ISubagentTaskExecutionStore _taskStore;
    private readonly IAgentChatRuntime _chatRuntime;
    private readonly ConversationTurnRecorder _turnRecorder;
    private readonly DesktopToolApprovalHandler _approvalHandler;
    private readonly SubagentTaskSnapshotSerializer _snapshotSerializer;
    private readonly ISubagentTaskPreflight _preflight;
    private readonly SubagentTaskExecutionRegistry _executionRegistry;
    private readonly SubagentActivityRegistry _activityRegistry;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<SubagentTaskExecutor> _logger;

    public SubagentTaskExecutor(
        IConversationRepository conversationRepository,
        ISubagentTaskExecutionStore taskStore,
        IAgentChatRuntime chatRuntime,
        ConversationTurnRecorder turnRecorder,
        DesktopToolApprovalHandler approvalHandler,
        SubagentTaskSnapshotSerializer snapshotSerializer,
        ISubagentTaskPreflight preflight,
        SubagentTaskExecutionRegistry executionRegistry,
        ILogger<SubagentTaskExecutor> logger,
        SubagentActivityRegistry? activityRegistry = null)
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
            logger,
            activityRegistry)
    {
    }

    internal SubagentTaskExecutor(
        IConversationRepository conversationRepository,
        ISubagentTaskExecutionStore taskStore,
        IAgentChatRuntime chatRuntime,
        ConversationTurnRecorder turnRecorder,
        DesktopToolApprovalHandler approvalHandler,
        SubagentTaskSnapshotSerializer snapshotSerializer,
        ISubagentTaskPreflight preflight,
        SubagentTaskExecutionRegistry executionRegistry,
        TimeProvider timeProvider,
        ILogger<SubagentTaskExecutor> logger,
        SubagentActivityRegistry? activityRegistry = null)
    {
        _conversationRepository = conversationRepository;
        _taskStore = taskStore;
        _chatRuntime = chatRuntime;
        _turnRecorder = turnRecorder;
        _approvalHandler = approvalHandler;
        _snapshotSerializer = snapshotSerializer;
        _preflight = preflight;
        _executionRegistry = executionRegistry;
        _activityRegistry = activityRegistry ?? new SubagentActivityRegistry();
        _timeProvider = timeProvider;
        _logger = logger;
    }

    internal async Task ExecuteAsync(SubagentTaskRecord task, CancellationToken hostCancellationToken)
    {
        ArgumentNullException.ThrowIfNull(task);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(task.MaxRunSeconds), _timeProvider);
        using var execution = CancellationTokenSource.CreateLinkedTokenSource(hostCancellationToken, timeout.Token);
        _executionRegistry.Register(task.Id, execution);
        if (task.CancelRequestedAtUtc is not null)
        {
            _executionRegistry.RequestCancellation(task.Id);
        }

        SubagentExecutionSession? session = null;
        try
        {
            var input = await LoadInputAsync(task, execution.Token).ConfigureAwait(false);
            session = await RegisterAsync(task, input).ConfigureAwait(false);
            await ExecuteSessionAsync(task, session, execution.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            session ??= await RegisterAsync(task, CreateFallbackInput(task)).ConfigureAwait(false);
            await session.BeginAsync().ConfigureAwait(false);
            await FinalizeCancellationAsync(task, session, timeout.IsCancellationRequested,
                hostCancellationToken.IsCancellationRequested).ConfigureAwait(false);
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Subagent task {TaskId} execution failed.", task.Id);
            session ??= await RegisterAsync(task, CreateFallbackInput(task)).ConfigureAwait(false);
            await session.BeginAsync().ConfigureAwait(false);
            var errorCode = exception switch
            {
                SubagentExecutionPreflightException preflight => preflight.ErrorCode,
                InvalidDataException => SubagentErrorCodes.SnapshotInvalid,
                _ => SubagentErrorCodes.ProviderFailed
            };
            await session.FinalizeAsync(SubagentTaskStatus.Failed, errorCode, exception.Message).ConfigureAwait(false);
        }
        finally
        {
            _executionRegistry.Unregister(task.Id);
            if (session is not null)
            {
                await _activityRegistry.UnregisterAsync(session).ConfigureAwait(false);
            }
        }
    }

    internal async Task RecoverInterruptedAsync(SubagentTaskRecord task, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(task);
        SubagentExecutionInput input;
        try
        {
            input = await LoadInputAsync(task, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Subagent task {TaskId} recovery could not load its child transcript.", task.Id);
            input = CreateFallbackInput(task);
        }

        var session = await RegisterAsync(task, input).ConfigureAwait(false);
        try
        {
            await session.BeginAsync(cancellationToken).ConfigureAwait(false);
            await session.FinalizeAsync(SubagentTaskStatus.Interrupted, SubagentErrorCodes.ProcessInterrupted,
                "The application stopped before the Subagent task reached a terminal state.").ConfigureAwait(false);
        }
        finally
        {
            await _activityRegistry.UnregisterAsync(session).ConfigureAwait(false);
        }
    }

    private Task<SubagentExecutionSession> RegisterAsync(SubagentTaskRecord task, SubagentExecutionInput input)
        => _activityRegistry.RegisterAsync(task, input, _turnRecorder, _taskStore, _timeProvider);

    private async Task ExecuteSessionAsync(SubagentTaskRecord task, SubagentExecutionSession session, CancellationToken cancellationToken)
    {
        await session.BeginAsync(cancellationToken).ConfigureAwait(false);
        var request = await CreateRequestAsync(task, session.ProviderMessages, session.InitialToolRuns, cancellationToken).ConfigureAwait(false);
        await foreach (var streamEvent in _chatRuntime.StreamTurnAsync(request, cancellationToken).ConfigureAwait(false))
        {
            await session.ApplyEventAsync(streamEvent, cancellationToken).ConfigureAwait(false);
        }

        if (!session.Activity.TerminalObserved)
        {
            await session.FinalizeAsync(SubagentTaskStatus.Failed, SubagentErrorCodes.ProviderFailed,
                "The Subagent stream ended without a terminal event.").ConfigureAwait(false);
        }
    }

    private async Task<DirectChatTurnRequest> CreateRequestAsync(
        SubagentTaskRecord task,
        IReadOnlyList<MessageRecord> messages,
        IReadOnlyList<ToolExecutionRecord> toolExecutions,
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
                CompletionBatch: null),
            toolExecutions);
    }

    private async Task<SubagentExecutionInput> LoadInputAsync(SubagentTaskRecord task, CancellationToken cancellationToken)
    {
        var conversation = await _conversationRepository.GetConversationAsync(task.ChildConversationId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("The Subagent child conversation is missing.");
        var messages = await _conversationRepository.ListMessagesAsync(task.ChildConversationId, cancellationToken).ConfigureAwait(false);
        var tools = await _conversationRepository.ListToolExecutionsAsync(task.ChildConversationId, cancellationToken).ConfigureAwait(false);
        return new SubagentExecutionInput(conversation, messages, tools);
    }

    private SubagentExecutionInput CreateFallbackInput(SubagentTaskRecord task)
    {
        var conversation = new ConversationRecord(task.ChildConversationId, $"Subagent: {task.SubagentName}",
            null, ConversationMode.Programming, ToolPermissionMode.RequireApproval, task.SubagentId,
            task.CreatedAtUtc, _timeProvider.GetUtcNow(), Kind: ConversationKind.Subagent, ParentConversationId: task.ParentConversationId);
        return new SubagentExecutionInput(conversation, [], []);
    }

    private Task FinalizeCancellationAsync(SubagentTaskRecord task, SubagentExecutionSession session, bool timedOut, bool applicationStopping)
    {
        var (status, errorCode, message) = _executionRegistry.IsCancellationRequested(task.Id)
            ? (SubagentTaskStatus.Cancelled, SubagentErrorCodes.CancelledByParent, "The Subagent task was cancelled by its parent.")
            : timedOut
                ? (SubagentTaskStatus.Failed, SubagentErrorCodes.TimedOut, $"The Subagent task exceeded its {task.MaxRunSeconds} second time limit.")
                : applicationStopping
                    ? (SubagentTaskStatus.Cancelled, SubagentErrorCodes.ApplicationStopping, "The application stopped while the Subagent task was running.")
                    : (SubagentTaskStatus.Failed, SubagentErrorCodes.RuntimeCancelled, "The Subagent runtime cancelled without a parent cancellation request.");
        return session.FinalizeAsync(status, errorCode, message);
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

}
