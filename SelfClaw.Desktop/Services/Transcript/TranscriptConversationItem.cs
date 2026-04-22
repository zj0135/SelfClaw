namespace SelfClaw.Desktop.Services;

public sealed record TranscriptConversationItem(
    string Id,
    string Title,
    string Timestamp,
    bool IsSelected,
    string? ParentId = null,
    int Depth = 0,
    bool IsAgentConversation = false,
    string? Badge = null,
    string? Subtitle = null,
    string? BoundAgentId = null);
