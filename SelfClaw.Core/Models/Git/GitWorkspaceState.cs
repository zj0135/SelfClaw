namespace SelfClaw.Core.Models;

public sealed record GitWorkspaceState(
    bool IsGitAvailable,
    bool IsRepository,
    string? Error,
    Guid? RepositoryId,
    string? RepositoryName,
    string? RepositoryRootPath,
    string? BranchName,
    string? HeadCommitSha,
    bool IsDetached,
    bool IsDirty,
    int? AheadCount,
    int? BehindCount,
    bool IsManagedWorktree,
    Guid? OwnerConversationId,
    string? BaseBranchName,
    bool HasMergeConflicts,
    IReadOnlyList<GitBranchInfo> Branches,
    IReadOnlyList<GitWorktreeInfo> Worktrees);
