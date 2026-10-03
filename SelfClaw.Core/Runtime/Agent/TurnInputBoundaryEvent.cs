using SelfClaw.Core.Models;

namespace SelfClaw.Core.Runtime.Agent;

public sealed record TurnInputBoundaryEvent(ConversationInputBatch Batch, TurnUsage? Usage) : AgentStreamEvent;
