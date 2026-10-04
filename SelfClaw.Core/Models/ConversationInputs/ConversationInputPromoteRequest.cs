namespace SelfClaw.Core.Models;

/// <summary>A revision-checked Queue → Steer conversion that keeps the input identity and sequence.</summary>
public sealed record ConversationInputPromoteRequest(Guid InputId, int ExpectedRevision, Guid TargetTurnId);
