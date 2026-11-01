namespace SelfClaw.Core.Models;

public sealed record GitWorkspaceChanges(
    bool IsGitAvailable,
    bool IsRepository,
    string? Error,
    string? RepositoryRootPath,
    string? BranchName,
    GitChangeSet Unstaged,
    GitChangeSet Staged);
