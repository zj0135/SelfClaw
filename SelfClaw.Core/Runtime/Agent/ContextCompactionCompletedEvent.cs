using SelfClaw.Core.Models;

namespace SelfClaw.Core.Runtime.Agent;

public sealed record ContextCompactionCompletedEvent(ConversationCompactionRecord Compaction) : AgentStreamEvent;
