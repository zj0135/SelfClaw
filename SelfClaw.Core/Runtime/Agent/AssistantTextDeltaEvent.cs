namespace SelfClaw.Core.Runtime.Agent;

/// <summary>A chunk of assistant body text. <paramref name="BlockId"/> groups deltas belonging to the same block.</summary>
public sealed record AssistantTextDeltaEvent(
    string? BlockId,
    string Delta) : AgentStreamEvent;
