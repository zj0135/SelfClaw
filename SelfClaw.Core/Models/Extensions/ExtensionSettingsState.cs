namespace SelfClaw.Core.Models;

public sealed record ExtensionSettingsState(
    long Revision,
    string? ActiveAgentId,
    IReadOnlyList<ExtensionAgentView> Agents,
    IReadOnlyList<ExtensionPackageView> Plugins,
    IReadOnlyList<ExtensionPackageView> Skills,
    IReadOnlyList<McpServerView> McpServers,
    IReadOnlyList<PluginPanelView> Panels);
