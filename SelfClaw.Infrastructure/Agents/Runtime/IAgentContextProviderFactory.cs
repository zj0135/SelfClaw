using Microsoft.Agents.AI;

namespace SelfClaw.Infrastructure.Agents.Runtime;

public interface IAgentContextProviderFactory
{
    IReadOnlyList<AIContextProvider> CreateProviders();
}
