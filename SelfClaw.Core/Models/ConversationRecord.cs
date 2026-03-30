namespace SelfClaw.Core.Models;

public sealed record ConversationRecord(
    Guid Id,
    string Title,
    Guid ProfileId,
    Guid? WorkspaceRootId,
    ToolPermissionMode ToolPermissionMode,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);
