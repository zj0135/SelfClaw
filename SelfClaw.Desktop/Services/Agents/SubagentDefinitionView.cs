namespace SelfClaw.Desktop.Services;

/// <summary>
/// 代理助手设置页使用的 Subagent 定义视图。
/// </summary>
public sealed record SubagentDefinitionView(
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
    bool IsValid,
    IReadOnlyList<string> Diagnostics);
