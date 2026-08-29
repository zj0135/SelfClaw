namespace SelfClaw.Desktop.Services.Plugins;

/// <summary>
/// The one context shape a panel sees. Both the pull (<c>getContext()</c>) and the push
/// (<c>context-changed</c>) carry this exact record, so a panel never has to reconcile two
/// half-populated views of the same state. Being a record also makes change detection free: the
/// publisher compares by value and stays quiet when nothing a panel can observe has moved.
/// </summary>
internal sealed record PluginPanelContext(
    string? ConversationId,
    string AgentId,
    string AgentName,
    string AgentMode,
    bool IsBusy,
    string? WorkspaceRootPath,
    string? WorkspaceRootName);
