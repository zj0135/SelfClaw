namespace SelfClaw.Desktop.Services.Runtime;

internal interface IRecordedTurnCommitter
{
    Task<bool> TryCommitAsync(RecordedTurnCommit commit);
}
