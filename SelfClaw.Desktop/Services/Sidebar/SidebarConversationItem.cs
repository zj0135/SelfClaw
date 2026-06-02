namespace SelfClaw.Desktop.Services;

public sealed record SidebarConversationItem(
    Guid Id,
    string Title,
    string Timestamp,
    bool IsSelected);
