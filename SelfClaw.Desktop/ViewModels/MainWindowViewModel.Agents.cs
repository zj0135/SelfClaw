using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Desktop.Services;

namespace SelfClaw.Desktop.ViewModels;

public sealed partial class MainWindowViewModel
{
    private void ReloadAgents()
    {
        _agents.Clear();
        _agents.AddRange(_desktopAgentStore.LoadAll());
        SelectAgentCore(null);
    }

    private DesktopAgentDefinition ResolveSelectedAgent()
        => ResolveAgent(_selectedAgentId);

    private DesktopAgentDefinition ResolveAgent(string? agentId)
    {
        var normalizedAgentId = NormalizeAgentId(agentId);
        return _agents.FirstOrDefault(item => string.Equals(item.Id, normalizedAgentId, StringComparison.OrdinalIgnoreCase))
               ?? _agents.FirstOrDefault(item => string.Equals(item.Id, DesktopAgentStore.BuildAgentId, StringComparison.OrdinalIgnoreCase))
               ?? _agents.First();
    }

    private void SelectAgentCore(string? agentId)
    {
        var nextAgent = ResolveAgent(agentId);
        _selectedAgentId = nextAgent.Id;
    }

    private void SyncSelectedAgentFromConversation(ConversationRecord conversation)
    {
        SelectAgentCore(conversation.AgentId);
    }

    private AgentRuntimeDefinition ResolveRuntimeAgent(string? agentId)
    {
        var agent = ResolveAgent(agentId);
        return new AgentRuntimeDefinition(
            agent.Id,
            agent.Name,
            agent.Description,
            AgentExecutionMode.Direct,
            agent.ToolPolicy,
            agent.EnabledSkills.ToArray(),
            [],
            [],
            agent.Instructions);
    }

    private string? ResolveConversationAgentName(ConversationRecord conversation)
        => ResolveAgent(conversation.AgentId).Name;

    private static string NormalizeAgentId(string? agentId)
        => agentId?.Trim().ToLowerInvariant() ?? string.Empty;

}
