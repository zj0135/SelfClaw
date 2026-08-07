namespace SelfClaw.Desktop.Services;

internal sealed record SubagentDefinition(
    string Id,
    string Name,
    string Description,
    Guid? ModelProfileId,
    string ToolPolicy,
    IReadOnlyList<string> PluginIds,
    IReadOnlyList<string> SkillIds,
    IReadOnlyList<string> McpServerIds,
    int MaxRunSeconds,
    string Instructions,
    string FilePath,
    bool IsValid,
    IReadOnlyList<string> Diagnostics);
