namespace SelfClaw.Core.Runtime.Agent;

/// <summary>
/// The context size at which this turn compacts, published once the turn knows its model and policy. It exists
/// for the usage meter: the displayed percentage is measured against the model window, and without this mark
/// the user cannot tell how close the conversation is to its next compaction. It is never stored.
/// </summary>
public sealed record ContextCompactionTriggerResolvedEvent(int TriggerTokens) : AgentStreamEvent;
