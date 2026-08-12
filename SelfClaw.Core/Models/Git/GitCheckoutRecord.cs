namespace SelfClaw.Core.Models;

public sealed record GitCheckoutRecord(
    Guid WorkspaceRootId,
    Guid RepositoryId,
    bool IsManaged,
    Guid? OwnerConversationId,
    Guid? SourceWorkspaceRootId,
    string BranchName,
    string? BaseBranchName,
    string? BaseCommitSha,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);
