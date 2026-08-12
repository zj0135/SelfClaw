using SelfClaw.Core.Models;

namespace SelfClaw.Core.Interfaces;

public interface IGitMergeManager
{
    Task<GitMergeResult> MergeAsync(
        WorkspaceRoot managedWorktree,
        CancellationToken cancellationToken = default);

    Task<GitWorkspaceState> AbortAsync(
        WorkspaceRoot managedWorktree,
        CancellationToken cancellationToken = default);
}
