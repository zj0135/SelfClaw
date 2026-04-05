namespace SelfClaw.Core.Models;

public sealed record ConversationRecord(
    Guid Id,
    string Title,
    Guid ProfileId,
    Guid? WorkspaceRootId,
    ConversationMode Mode,
    ToolPermissionMode ToolPermissionMode,
    int TeamMaxRounds,
    TeamOutputMode TeamOutputMode,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    Guid? ParentConversationId = null,
    Guid? RootConversationId = null,
    Guid? BoundAgentId = null,
    string? BoundAgentName = null,
    string? BoundAgentRole = null)
{
    public ConversationRecord(
        Guid id,
        string title,
        Guid profileId,
        Guid? workspaceRootId,
        ToolPermissionMode toolPermissionMode,
        DateTimeOffset createdAtUtc,
        DateTimeOffset updatedAtUtc)
        : this(
            id,
            title,
            profileId,
            workspaceRootId,
            ConversationMode.Programming,
            toolPermissionMode,
            TeamDiscussionDefaults.DefaultMaxRounds,
            TeamDiscussionDefaults.DefaultOutputMode,
            createdAtUtc,
            updatedAtUtc)
    {
    }

    public ConversationRecord(
        Guid id,
        string title,
        Guid profileId,
        Guid? workspaceRootId,
        ConversationMode mode,
        ToolPermissionMode toolPermissionMode,
        DateTimeOffset createdAtUtc,
        DateTimeOffset updatedAtUtc)
        : this(
            id,
            title,
            profileId,
            workspaceRootId,
            mode,
            toolPermissionMode,
            TeamDiscussionDefaults.DefaultMaxRounds,
            TeamDiscussionDefaults.DefaultOutputMode,
            createdAtUtc,
            updatedAtUtc)
    {
    }

    public bool IsTopLevelConversation => ParentConversationId is null;

    public bool IsAgentConversation => ParentConversationId is not null && !string.IsNullOrWhiteSpace(BoundAgentName);

    public Guid EffectiveRootConversationId => RootConversationId ?? ParentConversationId ?? Id;
}
