using SelfClaw.Desktop.Services.Agents.Definitions;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;

namespace SelfClaw.Desktop.ViewModels;

public sealed partial class MainWindowViewModel
{
    internal void ReloadAgents()
    {
        var selectedId = _selectedConversation?.AgentId ?? _selectedAgentId;
        _agents.Clear();
        _agents.AddRange(_agentSettings.ListAgents());
        SelectAgentCore(selectedId);
    }

    private DesktopAgentDefinition ResolveSelectedAgent()
        => ResolveAgent(_selectedAgentId);

    private DesktopAgentDefinition ResolveAgent(string? agentId)
    {
        var normalizedAgentId = NormalizeAgentId(agentId);
        return _agents.FirstOrDefault(item => string.Equals(item.Id, normalizedAgentId, StringComparison.OrdinalIgnoreCase))
               ?? new DesktopAgentDefinition(normalizedAgentId, $"{normalizedAgentId} (不可用)", "代理定义已删除或不可用。",
                   AgentExecutionMode.Direct, "none", [], [], [], [], string.Empty, string.Empty, false, ["代理定义不可用。"]);
    }

    private void SelectAgentCore(string? agentId)
    {
        var nextAgent = ResolveAgent(string.IsNullOrWhiteSpace(agentId) ? DesktopAgentDefinitionService.BuildAgentId : agentId);
        _selectedAgentId = nextAgent.Id;
    }

    private void SyncSelectedAgentFromConversation(ConversationRecord conversation)
    {
        SelectAgentCore(conversation.AgentId);
    }

    private AgentRuntimeDefinition ResolveRuntimeAgent(string? agentId)
    {
        var agent = ResolveAgent(agentId);
        if (!_agents.Any(item => string.Equals(item.Id, agent.Id, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"代理 '{agent.Id}' 已不可用，请恢复定义或选择其他代理新建会话。");
        return new AgentRuntimeDefinition(
            agent.Id,
            agent.Name,
            agent.Description,
            agent.Mode,
            agent.ToolPolicy,
            agent.PluginIds.ToArray(),
            agent.SkillIds.ToArray(),
            agent.McpServerIds.ToArray(),
            agent.SubagentIds.ToArray(),
            agent.Instructions);
    }

    private static string NormalizeAgentId(string? agentId)
        => agentId?.Trim().ToLowerInvariant() ?? string.Empty;

}
