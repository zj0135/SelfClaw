using System.Security.Cryptography;
using System.Text.Json;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Desktop.Services.Agents;

namespace SelfClaw.Desktop.Services.ConversationInputs;

internal static class ConversationInputSnapshots
{
    internal static ConversationInputExecutionSnapshot Capture(AgentSettingsService agents, string agentId,
        Guid modelId, Guid? rootId, string? rootPath, ToolPermissionMode permission)
    {
        var definition = ReadAgent(agents, agentId)
            ?? throw new InvalidOperationException(ConversationInputReason.AgentMissing);
        return new(agentId, agents.Revision, AgentExecutionMode.Direct, modelId, rootId, rootPath,
            permission, definition, Hash(definition));
    }

    internal static AgentRuntimeDefinition? ReadAgent(AgentSettingsService agents, string agentId)
    {
        var agent = agents.ListAgents().FirstOrDefault(item => string.Equals(item.Id, agentId, StringComparison.OrdinalIgnoreCase));
        return agent is null ? null : new(agent.Id, agent.Name, agent.Description, agent.Mode,
            agent.ToolPolicy, agent.PluginIds.ToArray(), agent.SkillIds.ToArray(), agent.McpServerIds.ToArray(),
            agent.SubagentIds.ToArray(), agent.Instructions);
    }

    internal static string Hash(AgentRuntimeDefinition definition)
        => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(definition)));
}
