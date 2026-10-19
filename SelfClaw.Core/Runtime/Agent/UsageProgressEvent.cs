using SelfClaw.Core.Models;

namespace SelfClaw.Core.Runtime.Agent;

/// <summary>
/// A mid-turn usage snapshot published for live display: the whole turn so far, not a delta.
/// Unlike <see cref="UsageReportedEvent"/> a producer may emit this as often as it likes, because no
/// consumer adds it to the turn accumulator. <see cref="UsageProgressEvent.Usage"/> therefore replaces
/// the displayed usage; the persisted turn usage still comes from the accumulated observations.
/// </summary>
/// <param name="IsEstimated">
/// The observation contains locally counted numbers a provider has not reported yet. It is display-only:
/// the recorder keeps it out of the turn record so an estimate can never reach the usage table.
/// </param>
public sealed record UsageProgressEvent(TurnUsage Usage, bool IsEstimated = false) : AgentStreamEvent;
