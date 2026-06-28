namespace SelfClaw.Core.Runtime.Agent;

/// <summary>A raw output line that the parser could not interpret. Preserved for diagnostics.</summary>
public sealed record RawOutputEvent(string Line) : AgentStreamEvent;
