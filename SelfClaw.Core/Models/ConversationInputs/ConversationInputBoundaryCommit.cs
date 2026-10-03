namespace SelfClaw.Core.Models;

public sealed record ConversationInputBoundaryCommit(
    ConversationInputBatch Batch,
    ConversationTurnCommit Content);
