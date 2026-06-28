namespace SelfClaw.Core.Runtime.Agent;

/// <summary>A chunk of assistant extended-thinking text. Rendered as a thinking segment.</summary>
public sealed record AssistantThinkingDeltaEvent(
    string? BlockId,
    string Delta) : AgentStreamEvent;
