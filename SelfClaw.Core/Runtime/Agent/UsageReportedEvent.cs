namespace SelfClaw.Core.Runtime.Agent;

/// <summary>Aggregated token usage reported by the agent, typically near run completion.</summary>
public sealed record UsageReportedEvent(
    int? InputTokens,
    int? OutputTokens) : AgentStreamEvent;
