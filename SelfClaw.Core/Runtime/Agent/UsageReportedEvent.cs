using SelfClaw.Core.Models;

namespace SelfClaw.Core.Runtime.Agent;

/// <summary>
/// Usage observed by the agent, typically near run completion. Each event carries one observation:
/// the Direct runtime aggregates the whole tool loop into a single observation, while CLI parsers
/// report one observation per parsed usage record and the recorder sums them.
/// </summary>
public sealed record UsageReportedEvent(TurnUsage Usage) : AgentStreamEvent;
