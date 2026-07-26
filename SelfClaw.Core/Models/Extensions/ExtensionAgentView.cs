namespace SelfClaw.Core.Models;

public sealed record ExtensionAgentView(
    string Id,
    string Name,
    IReadOnlyList<string> PluginIds,
    IReadOnlyList<string> SkillIds,
    IReadOnlyList<string> McpServerIds);
