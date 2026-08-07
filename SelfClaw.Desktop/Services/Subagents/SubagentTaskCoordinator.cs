using System.Text;
using System.IO;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Desktop.Services.Subagents.Models;

namespace SelfClaw.Desktop.Services.Subagents;

internal sealed class SubagentTaskCoordinator : ISubagentTaskCoordinator
{
    private const int MaximumTaskBytes = 32 * 1024;
    private const int MaximumErrorMessageLength = 2048;

    private readonly ISubagentTaskStore _taskStore;
    private readonly SubagentDefinitionCatalog _definitionCatalog;
    private readonly SubagentTaskSnapshotSerializer _snapshotSerializer;
    private readonly SubagentTaskPreflight _preflight;
    private readonly SubagentTaskWakeSignal _wakeSignal;
    private readonly SubagentTaskExecutionRegistry _executionRegistry;
    private readonly TimeProvider _timeProvider;

    public SubagentTaskCoordinator(
        ISubagentTaskStore taskStore,
        SubagentDefinitionCatalog definitionCatalog,
        SubagentTaskSnapshotSerializer snapshotSerializer,
        SubagentTaskPreflight preflight,
        SubagentTaskWakeSignal wakeSignal,
        SubagentTaskExecutionRegistry executionRegistry)
        : this(
            taskStore,
            definitionCatalog,
            snapshotSerializer,
            preflight,
            wakeSignal,
            executionRegistry,
            TimeProvider.System)
    {
    }

    internal SubagentTaskCoordinator(
        ISubagentTaskStore taskStore,
        SubagentDefinitionCatalog definitionCatalog,
        SubagentTaskSnapshotSerializer snapshotSerializer,
        SubagentTaskPreflight preflight,
        SubagentTaskWakeSignal wakeSignal,
        SubagentTaskExecutionRegistry executionRegistry,
        TimeProvider timeProvider)
    {
        _taskStore = taskStore;
        _definitionCatalog = definitionCatalog;
        _snapshotSerializer = snapshotSerializer;
        _preflight = preflight;
        _wakeSignal = wakeSignal;
        _executionRegistry = executionRegistry;
        _timeProvider = timeProvider;
    }

    public async Task<SubagentTaskView> StartAsync(
        SubagentTaskStartRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var subagentId = ValidateStartRequest(request);
        var definition = _definitionCatalog.Get(subagentId);
        var definitionSnapshot = CreateDefinitionSnapshot(subagentId, definition);
        var modelProfileId = definitionSnapshot.ModelProfileId ?? request.ParentModelProfileId;
        var failure = DefinitionFailure(subagentId, definition);
        if (failure is null)
        {
            failure = await _preflight.CheckAsync(
                definitionSnapshot,
                request,
                modelProfileId,
                cancellationToken);
        }

        var parentSnapshot = new SubagentParentExecutionSnapshot(
            1,
            request.ParentAgent,
            request.ParentModelProfileId,
            request.WorkspaceRoot,
            request.ToolPermissionMode,
            request.CapabilityCeiling);
        var creation = CreateTaskCreation(
            request.ParentConversationId,
            request.ParentTurnId,
            request.Task,
            definitionSnapshot,
            parentSnapshot,
            modelProfileId,
            attempt: 1,
            retryOfTaskId: null,
            failure);
        var created = await _taskStore.CreateAsync(creation, cancellationToken);
        if (created.Status == SubagentTaskStatus.Queued)
        {
            _wakeSignal.Signal();
        }

        return ToView(created);
    }

    public async Task<SubagentTaskView?> GetAsync(
        SubagentTaskQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var task = await _taskStore.GetAsync(query.ParentConversationId, query.TaskId, cancellationToken);
        return task is null ? null : ToView(task);
    }

    public async Task<SubagentTaskView> CancelAsync(
        SubagentTaskCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        while (true)
        {
            var task = await GetRequiredAsync(command.ParentConversationId, command.TaskId, cancellationToken);
            if (IsTerminal(task.Status))
            {
                return ToView(task);
            }

            var now = _timeProvider.GetUtcNow();
            if (task.Status == SubagentTaskStatus.Queued)
            {
                var completion = CreateTerminalCompletion(
                    task,
                    SubagentTaskStatus.Cancelled,
                    SubagentErrorCodes.CancelledByParent,
                    "The queued Subagent task was cancelled by its parent.",
                    now);
                var cancelled = await _taskStore.TryCompleteAsync(
                    task.Id,
                    SubagentTaskStatus.Queued,
                    completion,
                    cancellationToken);
                if (cancelled is not null)
                {
                    return ToView(cancelled);
                }

                continue;
            }

            var requested = await _taskStore.RequestCancellationAsync(
                command.ParentConversationId,
                command.TaskId,
                now,
                cancellationToken);
            if (requested is null)
            {
                continue;
            }

            _executionRegistry.RequestCancellation(task.Id);
            var current = await _taskStore.GetAsync(
                command.ParentConversationId,
                command.TaskId,
                cancellationToken);
            if (current is not null && IsTerminal(current.Status))
            {
                _executionRegistry.ClearCancellationRequest(task.Id);
                return ToView(current);
            }

            return ToView(requested);
        }
    }

    public async Task<SubagentTaskView> RetryAsync(
        SubagentTaskRetryRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ParentTurnId == Guid.Empty)
        {
            throw new ArgumentException("A retry requires the current parent turn id.", nameof(request));
        }

        var previous = await GetRequiredAsync(request.ParentConversationId, request.TaskId, cancellationToken);
        if (!IsTerminal(previous.Status))
        {
            throw new InvalidOperationException("Only a terminal Subagent task can be retried.");
        }

        var definition = _snapshotSerializer.DeserializeDefinition(previous.DefinitionSnapshotJson);
        var parent = _snapshotSerializer.DeserializeParent(previous.ParentExecutionSnapshotJson);
        var creation = CreateTaskCreation(
            request.ParentConversationId,
            request.ParentTurnId,
            previous.TaskText,
            definition,
            parent,
            previous.ResolvedModelProfileId
                ?? throw new InvalidDataException("The retried Subagent task has no resolved model snapshot."),
            previous.Attempt + 1,
            previous.Id,
            failure: null);
        var created = await _taskStore.CreateAsync(creation, cancellationToken);
        _wakeSignal.Signal();
        return ToView(created);
    }

    private string ValidateStartRequest(SubagentTaskStartRequest request)
    {
        if (request.ParentConversationId == Guid.Empty || request.ParentTurnId == Guid.Empty)
        {
            throw new ArgumentException("A Subagent task requires parent conversation and turn ids.", nameof(request));
        }

        if (request.ParentAgent.Mode != AgentExecutionMode.Direct)
        {
            throw new InvalidOperationException("Only a Direct parent Agent can delegate to a Subagent.");
        }

        if (request.ParentModelProfileId == Guid.Empty)
        {
            throw new ArgumentException("A concrete parent model profile is required.", nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.Task))
        {
            throw new ArgumentException("A Subagent task cannot be empty.", nameof(request));
        }

        if (Encoding.UTF8.GetByteCount(request.Task) > MaximumTaskBytes)
        {
            throw new ArgumentException("A Subagent task cannot exceed 32 KiB of UTF-8 text.", nameof(request));
        }

        var subagentId = request.SubagentId.Trim().ToLowerInvariant();
        var allowed = request.ParentAgent.SubagentIds.Contains(subagentId, StringComparer.OrdinalIgnoreCase) &&
                      request.CapabilityCeiling.SubagentIds.Contains(subagentId, StringComparer.OrdinalIgnoreCase);
        if (!allowed)
        {
            throw new UnauthorizedAccessException($"Subagent '{subagentId}' is not authorized for this Agent.");
        }

        return subagentId;
    }

    private SubagentTaskCreation CreateTaskCreation(
        Guid parentConversationId,
        Guid parentTurnId,
        string taskText,
        SubagentDefinitionSnapshot definition,
        SubagentParentExecutionSnapshot parent,
        Guid resolvedModelProfileId,
        int attempt,
        Guid? retryOfTaskId,
        SubagentPreflightFailure? failure)
    {
        var now = _timeProvider.GetUtcNow();
        var childConversationId = Guid.NewGuid();
        var childTurnId = Guid.NewGuid();
        var child = new ConversationRecord(
            childConversationId,
            $"Subagent: {definition.Name}",
            parent.WorkspaceRoot?.Id,
            ConversationMode.Programming,
            parent.ToolPermissionMode,
            definition.Id,
            now,
            now,
            Kind: ConversationKind.Subagent,
            ParentConversationId: parentConversationId);
        var taskMessage = new MessageRecord(
            Guid.NewGuid(),
            childConversationId,
            MessageRole.User,
            taskText,
            MessageStatus.Completed,
            now,
            now);
        var task = new SubagentTaskRecord(
            Guid.NewGuid(),
            parentConversationId,
            parentTurnId,
            childConversationId,
            childTurnId,
            definition.Id,
            definition.Name,
            taskText,
            SubagentTaskStatus.Queued,
            attempt,
            retryOfTaskId,
            _snapshotSerializer.Serialize(definition),
            _snapshotSerializer.Serialize(parent),
            resolvedModelProfileId,
            definition.MaxRunSeconds,
            FinalText: null,
            InputTokens: null,
            OutputTokens: null,
            ErrorCode: null,
            ErrorMessage: null,
            CancelRequestedAtUtc: null,
            QueuedAtUtc: now,
            StartedAtUtc: null,
            CompletedAtUtc: null,
            CreatedAtUtc: now,
            UpdatedAtUtc: now);
        var initialCompletion = failure is null
            ? null
            : CreateTerminalCompletion(
                task,
                SubagentTaskStatus.Failed,
                failure.ErrorCode,
                failure.ErrorMessage,
                now);
        return new SubagentTaskCreation(child, taskMessage, task, initialCompletion);
    }

    private static SubagentTaskCompletion CreateTerminalCompletion(
        SubagentTaskRecord task,
        SubagentTaskStatus status,
        string errorCode,
        string errorMessage,
        DateTimeOffset completedAtUtc)
    {
        var messageStatus = status == SubagentTaskStatus.Cancelled
            ? MessageStatus.Cancelled
            : MessageStatus.Failed;
        var normalizedError = NormalizeErrorMessage(errorMessage);
        var assistant = new MessageRecord(
            task.ChildTurnId,
            task.ChildConversationId,
            MessageRole.Assistant,
            string.Empty,
            messageStatus,
            completedAtUtc,
            completedAtUtc,
            AgentName: task.SubagentName,
            AgentRole: "Subagent",
            ErrorMessage: normalizedError);
        return new SubagentTaskCompletion(
            status,
            new TurnFinalization(assistant, []),
            FinalText: null,
            errorCode,
            normalizedError,
            completedAtUtc);
    }

    private static SubagentDefinitionSnapshot CreateDefinitionSnapshot(
        string subagentId,
        SubagentDefinition? definition)
        => definition is null
            ? new SubagentDefinitionSnapshot(
                1,
                subagentId,
                subagentId,
                string.Empty,
                null,
                SubagentDefinitionCatalog.DefaultToolPolicy,
                [],
                [],
                [],
                SubagentDefinitionCatalog.DefaultMaxRunSeconds,
                string.Empty)
            : new SubagentDefinitionSnapshot(
                1,
                definition.Id,
                NormalizeDefinitionName(definition.Name, subagentId),
                definition.Description,
                definition.ModelProfileId,
                definition.ToolPolicy,
                definition.PluginIds,
                definition.SkillIds,
                definition.McpServerIds,
                definition.MaxRunSeconds,
                definition.Instructions);

    private static SubagentPreflightFailure? DefinitionFailure(
        string subagentId,
        SubagentDefinition? definition)
    {
        if (definition is null)
        {
            return new SubagentPreflightFailure(
                SubagentErrorCodes.DefinitionMissing,
                $"Subagent definition '{subagentId}' was not found.");
        }

        return definition.IsValid
            ? null
            : new SubagentPreflightFailure(
                SubagentErrorCodes.DefinitionInvalid,
                NormalizeErrorMessage(string.Join(" ", definition.Diagnostics)));
    }

    private async Task<SubagentTaskRecord> GetRequiredAsync(
        Guid parentConversationId,
        Guid taskId,
        CancellationToken cancellationToken)
        => await _taskStore.GetAsync(parentConversationId, taskId, cancellationToken)
            ?? throw new KeyNotFoundException("The Subagent task was not found.");

    private static SubagentTaskView ToView(SubagentTaskRecord task)
        => new(
            task.Id,
            task.ParentConversationId,
            task.ParentTurnId,
            task.ChildConversationId,
            task.ChildTurnId,
            task.SubagentId,
            task.SubagentName,
            task.TaskText,
            task.Status,
            task.Attempt,
            task.RetryOfTaskId,
            task.ResolvedModelProfileId,
            task.FinalText,
            task.InputTokens,
            task.OutputTokens,
            task.ErrorCode,
            task.ErrorMessage,
            task.QueuedAtUtc,
            task.StartedAtUtc,
            task.CompletedAtUtc,
            task.CreatedAtUtc,
            task.UpdatedAtUtc);

    private static string NormalizeErrorMessage(string message)
        => message.Length <= MaximumErrorMessageLength
            ? message
            : message[..MaximumErrorMessageLength];

    private static string NormalizeDefinitionName(string name, string fallback)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return fallback;
        }

        var builder = new StringBuilder();
        var bytes = 0;
        foreach (var rune in name.EnumerateRunes())
        {
            var runeBytes = rune.Utf8SequenceLength;
            if (bytes + runeBytes > 256)
            {
                break;
            }

            builder.Append(rune.ToString());
            bytes += runeBytes;
        }

        return builder.Length == 0 ? fallback : builder.ToString();
    }

    private static bool IsTerminal(SubagentTaskStatus status)
        => status is SubagentTaskStatus.Succeeded
            or SubagentTaskStatus.Failed
            or SubagentTaskStatus.Cancelled
            or SubagentTaskStatus.Interrupted;
}
