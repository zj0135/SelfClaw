using SelfClaw.Core.Models;

namespace SelfClaw.Core.Runtime;

public sealed record TeamAgentsPlannedEvent(
    IReadOnlyList<TeamAgentRecord> Agents) : ChatRuntimeEvent;
