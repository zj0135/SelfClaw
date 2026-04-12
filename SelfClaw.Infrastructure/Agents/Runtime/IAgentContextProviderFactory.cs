using Microsoft.Agents.AI;

namespace SelfClaw.Infrastructure.Agents;

public interface IAgentContextProviderFactory
{
    IReadOnlyList<AIContextProvider> CreateProviders();
}
