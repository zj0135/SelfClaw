using SelfClaw.Core.Runtime;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Desktop.Services.Runtime;

namespace SelfClaw.Desktop.Services.Subagents;

internal sealed class SubagentChildTurnCommitter : IRecordedTurnCommitter
{
    private readonly ISubagentTaskExecutionStore _taskStore;
    private readonly Guid _taskId;
    private readonly TimeProvider _timeProvider;
    private SubagentTaskStatus? _overrideStatus;
    private string? _overrideErrorCode;
    private string? _overrideErrorMessage;

    internal SubagentChildTurnCommitter(
        ISubagentTaskExecutionStore taskStore,
        Guid taskId,
        TimeProvider timeProvider)
    {
        _taskStore = taskStore;
        _taskId = taskId;
        _timeProvider = timeProvider;
    }

    internal void OverrideTerminal(
        SubagentTaskStatus status,
        string errorCode,
        string errorMessage)
    {
        _overrideStatus = status;
        _overrideErrorCode = errorCode;
        _overrideErrorMessage = errorMessage;
    }

    public async Task<bool> TryCommitAsync(RecordedTurnCommit commit)
    {
        ArgumentNullException.ThrowIfNull(commit);
        var status = _overrideStatus ?? commit.Finalization.Turn.Status switch
        {
            ConversationTurnStatus.Succeeded => SubagentTaskStatus.Succeeded,
            ConversationTurnStatus.Failed or ConversationTurnStatus.Blocked or ConversationTurnStatus.Truncated => SubagentTaskStatus.Failed,
            ConversationTurnStatus.Cancelled => SubagentTaskStatus.Cancelled,
            ConversationTurnStatus.Interrupted => SubagentTaskStatus.Interrupted,
            _ => throw new ArgumentOutOfRangeException(nameof(commit), commit.Finalization.Turn.Status, null)
        };
        var errorCode = status == SubagentTaskStatus.Succeeded
            ? null
            : _overrideErrorCode ?? commit.Finalization.Turn.Status switch
            {
                ConversationTurnStatus.Truncated => SubagentErrorCodes.OutputTruncated,
                ConversationTurnStatus.Blocked => SubagentErrorCodes.BlockedByHook,
                _ => SubagentErrorCodes.ProviderFailed
            };
        var errorMessage = status == SubagentTaskStatus.Succeeded
            ? null
            : NormalizeError(_overrideErrorMessage ?? commit.ErrorMessage ?? DefaultError(commit.Finalization.Turn.Status));
        var completion = new SubagentTaskCompletion(
            status,
            commit.Finalization,
            commit.FinalText,
            errorCode,
            errorMessage,
            _timeProvider.GetUtcNow());
        var completed = await _taskStore.TryCompleteAsync(
            _taskId,
            SubagentTaskStatus.Running,
            completion);
        return completed is not null;
    }

    private static string DefaultError(ConversationTurnStatus status)
        => status switch
        {
            ConversationTurnStatus.Truncated => "The Subagent reached the model output limit. Partial output was preserved.",
            ConversationTurnStatus.Blocked => "The Subagent run was blocked.",
            _ => "The Subagent run failed."
        };

    private static string NormalizeError(string message)
        => message.Length <= 2048 ? message : message[..2048];
}
