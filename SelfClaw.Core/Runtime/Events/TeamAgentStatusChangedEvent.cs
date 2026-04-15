using SelfClaw.Core.Models;

namespace SelfClaw.Core.Runtime;

public sealed record TeamAgentStatusChangedEvent(
    Guid AgentId,
    TeamAgentStatus Status) : ChatRuntimeEvent;
