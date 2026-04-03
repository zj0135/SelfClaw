namespace SelfClaw.Infrastructure.Agents;

internal interface IAgentExecutionService
{
    Task<AgentExecutionResult> RunAsync(
        AgentExecutionRequest request,
        Func<string, CancellationToken, ValueTask>? onTextDelta,
        CancellationToken cancellationToken);
}
