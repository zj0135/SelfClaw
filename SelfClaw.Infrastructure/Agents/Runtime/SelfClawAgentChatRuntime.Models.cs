using Microsoft.Agents.AI;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;

namespace SelfClaw.Infrastructure.Agents.Runtime;

public sealed partial class SelfClawAgentChatRuntime
{
    private sealed record TeamPlan(
        IReadOnlyList<TeamAgentRecord> Agents,
        TeamAgentRecord Coordinator,
        string DocumentTitle);

    private sealed record TeamBlueprint(
        string DocumentTitle,
        IReadOnlyList<TeamBlueprintAgent> Agents);

    private sealed record TeamBlueprintAgent(
        string Name,
        string Role,
        string Mission);

    private sealed record DiscussionEntry(
        int RoundNumber,
        TeamAgentRecord Agent,
        string Markdown,
        bool Succeeded,
        string? ErrorMessage);

    private sealed record ExecutionPlanBlueprint(
        string? Summary,
        IReadOnlyList<ExecutionPlanBlueprintStep> Steps);

    private sealed record ExecutionPlanBlueprintStep(
        string? Id,
        string Title);

    private sealed record CompletedExecutionPlanStep(
        ExecutionPlanStep Step,
        string Markdown);

    private sealed record DocumentDecision(
        bool ShouldExportDocument);

    private sealed record RoundContinuationDecision(
        bool ContinueDiscussion);

    private sealed class EmptyAgentContextProviderFactory : IAgentContextProviderFactory
    {
        public IReadOnlyList<AIContextProvider> CreateProviders() => [];
    }
}
