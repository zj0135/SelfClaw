using Microsoft.Agents.AI;
using SelfClaw.Core.Runtime;

namespace SelfClaw.Infrastructure.Agents.Runtime.Context;

public interface IAgentContextProviderFactory
{
    IReadOnlyList<AIContextProvider> CreateProviders(AgentRuntimeDefinition agent);
}
