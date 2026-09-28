using SelfClaw.Core.Models;

namespace SelfClaw.Core.Interfaces;

public interface IGitWorkspaceManager
{
    Task<ManagedGitWorktreeCreation> CreateManagedWorktreeAsync(
        WorkspaceRoot sourceWorkspace,
        Guid conversationId,
        string prompt,
        CancellationToken cancellationToken = default);

    Task<GitWorkspaceState> CreateBranchAsync(
        WorkspaceRoot workspaceRoot,
        string branchName,
        string? startPoint = null,
        CancellationToken cancellationToken = default);

    Task<GitWorkspaceState> SwitchBranchAsync(
        WorkspaceRoot workspaceRoot,
        string branchName,
        CancellationToken cancellationToken = default);

    Task<GitWorkspaceState> DeleteBranchAsync(
        WorkspaceRoot workspaceRoot,
        string branchName,
        CancellationToken cancellationToken = default);

    Task RemoveManagedWorktreeAsync(
        WorkspaceRoot workspaceRoot,
        CancellationToken cancellationToken = default);

    // Escape hatch for worktrees whose recorded branch name no longer matches Git (for example a
    // mojibake name created before branch names were forced ASCII-safe). It still refuses to touch
    // anything outside SelfClaw's worktree directory, and it discards local changes.
    Task ForceRemoveManagedWorktreeAsync(
        WorkspaceRoot workspaceRoot,
        CancellationToken cancellationToken = default);
}
