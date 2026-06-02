using System.Collections.ObjectModel;

namespace SelfClaw.Desktop.Services;

public sealed record SidebarProjectItem(
    Guid Id,
    string Name,
    string RootPath,
    bool IsExpanded,
    ObservableCollection<SidebarConversationItem> Conversations);
