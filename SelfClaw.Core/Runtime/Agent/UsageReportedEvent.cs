using SelfClaw.Core.Models;

namespace SelfClaw.Core.Runtime.Agent;

/// <summary>
/// Usage observed by the agent, typically near run completion. Each event carries one observation:
/// the Direct runtime aggregates the whole tool loop into a single observation, while CLI parsers
/// report one observation per parsed usage record and the recorder sums them.
/// Producers normalize optional totals before emitting the event. A null total is explicitly unknown
/// and must remain unknown in the recorder and persistence layers.
/// </summary>
public sealed record UsageReportedEvent(TurnUsage Usage) : AgentStreamEvent;
