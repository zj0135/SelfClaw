namespace SelfClaw.Core.Models;

public sealed record SubagentContentQuery(
    Guid ParentConversationId,
    Guid TaskId,
    string ContentVersion,
    string ContentId,
    int Offset = 0,
    int MaximumCharacters = 8192);
