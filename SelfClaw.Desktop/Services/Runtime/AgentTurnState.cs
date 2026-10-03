using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Desktop.Services.ConversationInputs;

namespace SelfClaw.Desktop.Services.Runtime;

internal sealed class AgentTurnState
{
    public AgentTurnState(
        ConversationTurnRecord turn,
        AgentRuntimeDefinition agent,
        DirectTurnInputSession? inputSession = null)
    {
        ArgumentNullException.ThrowIfNull(turn);
        ArgumentNullException.ThrowIfNull(agent);
        if (turn.Id == Guid.Empty || turn.ConversationId == Guid.Empty)
            throw new ArgumentException("A turn requires independent turn and conversation identities.", nameof(turn));

        Record = turn;
        AgentName = agent.Name;
        AgentRole = "Agent";
        InputSession = inputSession;
    }

    public ConversationTurnRecord Record { get; }
    public Guid TurnId => Record.Id;
    public DateTimeOffset StartedAtUtc => Record.StartedAtUtc;
    public string AgentName { get; }
    public string AgentRole { get; }
    public DirectTurnInputSession? InputSession { get; }
    public TurnUsageAccumulator Usage { get; } = new();
    public Guid? CurrentAssistantMessageId { get; set; }
    public bool HasVisibleDelta { get; set; }
    public bool Completed { get; set; }
    public RecordedTurnFinalizationRequest? PendingFinalization { get; set; }
    public Dictionary<string, ToolExecutionRecord> ToolRunsByCallId { get; } = new(StringComparer.Ordinal);
}
