namespace SelfClaw.Core.Runtime.Agent;

/// <summary>
/// Emitted once when a run begins. Carries the session id (captured from the CLI
/// stream or assigned by us) and the resolved model, used to persist resume state.
/// </summary>
public sealed record RunStartedEvent(
    string? SessionId,
    string? Model,
    CliAgentKind AgentKind) : AgentStreamEvent;
