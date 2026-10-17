namespace SelfClaw.Core.Runtime.Agent;

/// <summary>Emitted before the summarization call so the UI can show compaction activity.</summary>
public sealed record ContextCompactionStartedEvent(int EstimatedTokens, int ThresholdTokens) : AgentStreamEvent;
