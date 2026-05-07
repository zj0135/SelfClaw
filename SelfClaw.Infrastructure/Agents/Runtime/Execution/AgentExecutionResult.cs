namespace SelfClaw.Infrastructure.Agents.Runtime.Execution;

internal sealed record AgentExecutionResult(
    string FinalMarkdown,
    int? InputTokens,
    int? OutputTokens,
    TimeSpan Duration);
