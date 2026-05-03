using Microsoft.Agents.AI;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;

namespace SelfClaw.Infrastructure.Agents.Runtime;

public sealed partial class SelfClawAgentChatRuntime
{
    private sealed record ExecutionPlanBlueprint(
        string? Summary,
        IReadOnlyList<ExecutionPlanBlueprintStep> Steps);

    private sealed record ExecutionPlanBlueprintStep(
        string? Id,
        string Title);

    private sealed record CompletedExecutionPlanStep(
        ExecutionPlanStep Step,
        string Markdown);

    private sealed class EmptyAgentContextProviderFactory : IAgentContextProviderFactory
    {
        public IReadOnlyList<AIContextProvider> CreateProviders() => [];
    }
}
