namespace SelfClaw.Core.Models;

/// <summary>
/// The Agent configuration an automation run actually used. It is persisted on the run so that editing
/// the task later cannot retroactively change how an earlier run is explained.
/// </summary>
public sealed record AutomationCapabilitySnapshot(
    int Version,
    string ToolPolicy,
    IReadOnlyList<string> PluginIds,
    IReadOnlyList<string> SkillIds,
    IReadOnlyList<string> McpServerIds,
    string Instructions);
