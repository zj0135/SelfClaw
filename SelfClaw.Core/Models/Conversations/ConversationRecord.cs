namespace SelfClaw.Core.Models;

public sealed record ConversationRecord(
    Guid Id,
    string Title,
    Guid ProfileId,
    Guid? WorkspaceRootId,
    ConversationMode Mode,
    ToolPermissionMode ToolPermissionMode,
    string AgentId,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    string? ChannelKind = null,
    string? ChannelConversationId = null,
    string? ChannelDisplayName = null)
{
    public ConversationRecord(
        Guid id,
        string title,
        Guid profileId,
        Guid? workspaceRootId,
        ToolPermissionMode toolPermissionMode,
        string agentId,
        DateTimeOffset createdAtUtc,
        DateTimeOffset updatedAtUtc)
        : this(
            id,
            title,
            profileId,
            workspaceRootId,
            ConversationMode.Programming,
            toolPermissionMode,
            agentId,
            createdAtUtc,
            updatedAtUtc)
    {
    }

}
