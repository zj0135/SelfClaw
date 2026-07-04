namespace SelfClaw.Desktop.Services;

public sealed record TranscriptConversationItem(
    string Id,
    string Title,
    string Timestamp,
    bool IsSelected,
    string? Badge = null,
    string? Subtitle = null,
    string? AgentId = null,
    string? AgentName = null,
    string? WorkspaceRootId = null,
    string? WorkspaceRootName = null,
    string? WorkspaceRootPath = null);
