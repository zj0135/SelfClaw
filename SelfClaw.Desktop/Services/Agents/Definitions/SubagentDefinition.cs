namespace SelfClaw.Desktop.Services.Agents.Definitions;

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
    IReadOnlyList<string> Diagnostics)
{
    /// <summary>Derived from the id by the catalog: built-in definitions are recreated when missing and cannot be deleted.</summary>
    public bool IsBuiltIn { get; init; }
}
