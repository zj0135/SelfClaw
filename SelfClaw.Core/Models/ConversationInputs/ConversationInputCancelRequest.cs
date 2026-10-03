namespace SelfClaw.Core.Models;

public sealed record ConversationInputCancelRequest(
    Guid InputId,
    int ExpectedRevision);
