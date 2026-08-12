namespace SelfClaw.Core.Models;

public sealed record GitWorktreeInfo(
    string Path,
    string CommitSha,
    string? BranchName,
    bool IsDetached,
    bool IsCurrent,
    bool IsManaged,
    Guid? OwnerConversationId);
