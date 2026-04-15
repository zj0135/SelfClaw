namespace SelfClaw.Infrastructure.Agents.Runtime;

internal sealed record AgentExecutionResult(
    string FinalMarkdown,
    int? InputTokens,
    int? OutputTokens,
    TimeSpan Duration);
