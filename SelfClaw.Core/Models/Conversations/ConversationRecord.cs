namespace SelfClaw.Core.Models;

public sealed record ConversationRecord(
    Guid Id,
    string Title,
    Guid? WorkspaceRootId,
    ConversationMode Mode,
    ToolPermissionMode ToolPermissionMode,
    string AgentId,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    string? ChannelKind = null,
    string? ChannelConversationId = null,
    string? ChannelDisplayName = null,
    ConversationKind Kind = ConversationKind.Interactive,
    Guid? ParentConversationId = null)
{
    public ConversationRecord(
        Guid id,
        string title,
        Guid? workspaceRootId,
        ToolPermissionMode toolPermissionMode,
        string agentId,
        DateTimeOffset createdAtUtc,
        DateTimeOffset updatedAtUtc)
        : this(
            id,
            title,
            workspaceRootId,
            ConversationMode.Programming,
            toolPermissionMode,
            agentId,
            createdAtUtc,
            updatedAtUtc)
    {
    }

}
