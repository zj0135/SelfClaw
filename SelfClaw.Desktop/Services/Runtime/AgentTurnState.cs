using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;

namespace SelfClaw.Desktop.Services.Runtime;

/// <summary>Per-turn reduction state shared across the events of a single <c>StreamTurnAsync</c> call.</summary>
internal sealed class AgentTurnState
{
    public AgentTurnState(Guid turnId, AgentRuntimeDefinition agent)
    {
        ArgumentNullException.ThrowIfNull(agent);

        if (turnId == Guid.Empty)
        {
            throw new ArgumentException("Turn id cannot be empty.", nameof(turnId));
        }

        TurnId = turnId;
        AgentName = agent.Name;
        AgentRole = "Agent";
    }

    public Guid TurnId { get; }

    public string AgentName { get; }

    public string AgentRole { get; }

    public DateTimeOffset StartedAtUtc { get; } = DateTimeOffset.UtcNow;

    public int? InputTokens { get; set; }

    public int? OutputTokens { get; set; }

    public bool MessageCreated { get; set; }

    /// <summary>
    /// Whether any text or thinking content has streamed for this turn. The first visible delta
    /// publishes immediately so the user sees output without waiting out the coalescing interval.
    /// </summary>
    public bool HasVisibleDelta { get; set; }

    public bool Completed { get; set; }

    public RecordedTurnFinalizationRequest? PendingFinalization { get; set; }

    public Dictionary<string, ToolExecutionRecord> ToolRunsByCallId { get; } = new(StringComparer.Ordinal);
}
