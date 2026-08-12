namespace SelfClaw.Desktop.Services;

/// <summary>
/// 代理助手设置页使用的 Agent 定义视图。Mode 以字符串（"direct"/"cli"）下发，
/// 避免依赖宿主 JSON 管道的枚举序列化约定。
/// </summary>
public sealed record AgentDefinitionView(
    string Id,
    string Name,
    string Description,
    string Mode,
    IReadOnlyList<string> PluginIds,
    IReadOnlyList<string> SkillIds,
    IReadOnlyList<string> McpServerIds,
    IReadOnlyList<string> SubagentIds,
    string Instructions,
    bool IsBuiltIn,
    IReadOnlyList<string> Warnings);
