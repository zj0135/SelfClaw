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
        var status = _overrideStatus ?? commit.Kind switch
        {
            TurnFinalizationKind.Succeeded => SubagentTaskStatus.Succeeded,
            TurnFinalizationKind.Failed => SubagentTaskStatus.Failed,
            TurnFinalizationKind.Cancelled => SubagentTaskStatus.Cancelled,
            _ => throw new ArgumentOutOfRangeException(nameof(commit), commit.Kind, null)
        };
        var errorCode = status == SubagentTaskStatus.Succeeded
            ? null
            : _overrideErrorCode ?? SubagentErrorCodes.ProviderFailed;
        var errorMessage = status == SubagentTaskStatus.Succeeded
            ? null
            : NormalizeError(_overrideErrorMessage ?? commit.ErrorMessage ?? "The Subagent run failed.");
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

    private static string NormalizeError(string message)
        => message.Length <= 2048 ? message : message[..2048];
}
