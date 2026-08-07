namespace SelfClaw.Desktop.Services.Subagents.Models;

internal sealed record SubagentDefinitionSnapshot(
    int Version,
    string Id,
    string Name,
    string Description,
    Guid? ModelProfileId,
    string ToolPolicy,
    IReadOnlyList<string> PluginIds,
    IReadOnlyList<string> SkillIds,
    IReadOnlyList<string> McpServerIds,
    int MaxRunSeconds,
    string Instructions);
