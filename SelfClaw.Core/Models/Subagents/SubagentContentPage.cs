namespace SelfClaw.Core.Models;

public sealed record SubagentContentPage(
    Guid TaskId,
    string ContentVersion,
    string ContentId,
    string Text,
    int Offset,
    int? NextOffset,
    int TotalCharacters);
