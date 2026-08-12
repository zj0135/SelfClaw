namespace SelfClaw.Desktop.Services;

public sealed record TranscriptConversationItem(
    string Id,
    string Title,
    string Timestamp,
    string? WorkspaceRootId = null,
    string? WorkspaceRootName = null,
    string? WorkspaceRootPath = null,
    string? GitRepositoryId = null,
    string? GitRepositoryName = null,
    string? GitBranchName = null,
    bool IsManagedWorktree = false);
