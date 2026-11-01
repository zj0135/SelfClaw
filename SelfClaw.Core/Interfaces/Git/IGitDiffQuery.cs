using SelfClaw.Core.Models;

namespace SelfClaw.Core.Interfaces;

public interface IGitDiffQuery
{
    Task<GitWorkspaceChanges> GetChangesAsync(
        WorkspaceRoot workspaceRoot,
        CancellationToken cancellationToken = default);

    Task<GitFileDiff> GetFileDiffAsync(
        WorkspaceRoot workspaceRoot,
        GitChangeScope scope,
        string path,
        string? oldPath,
        GitChangeKind kind,
        CancellationToken cancellationToken = default);
}
