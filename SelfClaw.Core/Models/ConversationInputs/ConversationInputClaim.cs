namespace SelfClaw.Core.Models;

/// <summary>
/// Durable claim facts for a single FollowUp. The start transaction must match these before it
/// writes the Running turn, the owning user message and the Consumed mapping.
/// </summary>
public sealed record ConversationInputClaim(
    Guid InputId,
    Guid ConversationId,
    int Revision,
    Guid ClaimId,
    Guid OwnerRunId,
    Guid MessageId);
