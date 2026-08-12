using SelfClaw.Core.Models;

namespace SelfClaw.Desktop.Services;

/// <summary>
/// 代理助手设置页的完整状态快照：Agent/Subagent 定义加上可用于绑定维护的扩展目录。
/// </summary>
public sealed record AgentSettingsState(
    long Revision,
    IReadOnlyList<AgentDefinitionView> Agents,
    IReadOnlyList<SubagentDefinitionView> Subagents,
    IReadOnlyList<ExtensionPackageView> Plugins,
    IReadOnlyList<ExtensionPackageView> Skills,
    IReadOnlyList<McpServerView> McpServers);
