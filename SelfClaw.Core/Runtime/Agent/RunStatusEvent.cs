namespace SelfClaw.Core.Runtime.Agent;

/// <summary>High-level lifecycle phase of the run (initializing, thinking, requesting, ...).</summary>
public sealed record RunStatusEvent(AgentRunStatus Status) : AgentStreamEvent;
