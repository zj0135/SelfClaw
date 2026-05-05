using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
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

    private sealed class AgentToolScope : IAsyncDisposable
    {
        private readonly IAsyncDisposable? _ownedResources;

        public AgentToolScope(
            IList<AITool> tools,
            IReadOnlyDictionary<string, ToolInvocationMetadata>? toolMetadata = null,
            IAsyncDisposable? ownedResources = null)
        {
            Tools = tools;
            ToolMetadata = toolMetadata ?? new Dictionary<string, ToolInvocationMetadata>(StringComparer.OrdinalIgnoreCase);
            _ownedResources = ownedResources;
        }

        public IList<AITool> Tools { get; }

        public IReadOnlyDictionary<string, ToolInvocationMetadata> ToolMetadata { get; }

        public ValueTask DisposeAsync()
            => _ownedResources?.DisposeAsync() ?? ValueTask.CompletedTask;
    }

    private sealed class EmptyAgentContextProviderFactory : IAgentContextProviderFactory
    {
        public IReadOnlyList<AIContextProvider> CreateProviders(AgentRuntimeDefinition agent) => [];
    }

    private sealed class EmptyAgentMcpToolProvider : IAgentMcpToolProvider
    {
        public Task<ResolvedMcpTools> CreateToolsAsync(
            ChatTurnRequest request,
            CancellationToken cancellationToken)
            => Task.FromResult(ResolvedMcpTools.Empty);
    }
}
