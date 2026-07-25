using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;

namespace SelfClaw.Desktop.Services.Runtime;

/// <summary>Per-turn reduction state shared across the events of a single <c>StreamTurnAsync</c> call.</summary>
internal sealed class AgentTurnState
{
    public AgentTurnState(AgentRuntimeDefinition agent)
    {
        AssistantMessageId = Guid.NewGuid();
        AgentName = agent.Name;
        AgentRole = "Agent";
    }

    public Guid AssistantMessageId { get; }

    public Guid? AgentId => null;

    public string AgentName { get; }

    public string AgentRole { get; }

    public DateTimeOffset StartedAtUtc { get; } = DateTimeOffset.UtcNow;

    public int? InputTokens { get; set; }

    public int? OutputTokens { get; set; }

    public bool MessageCreated { get; set; }

    public bool Completed { get; set; }

    public DesktopTurnFinalizationRequest? PendingFinalization { get; set; }

    public Dictionary<string, ToolExecutionRecord> ToolRunsByCallId { get; } = new(StringComparer.Ordinal);
}
