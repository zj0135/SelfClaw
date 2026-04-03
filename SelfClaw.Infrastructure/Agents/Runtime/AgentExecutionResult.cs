namespace SelfClaw.Infrastructure.Agents;

internal sealed record AgentExecutionResult(
    string FinalMarkdown,
    int? InputTokens,
    int? OutputTokens,
    TimeSpan Duration);
