namespace SelfClaw.Core.Models;

public sealed record WorkspaceRoot(
    Guid Id,
    string Name,
    string RootPath,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    Guid? GitRepositoryId = null,
    string? GitRepositoryName = null,
    string? GitBranchName = null,
    bool IsManagedWorktree = false,
    Guid? ManagedConversationId = null,
    string? BaseBranchName = null);
