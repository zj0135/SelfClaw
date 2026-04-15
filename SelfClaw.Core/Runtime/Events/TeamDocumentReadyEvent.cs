namespace SelfClaw.Core.Runtime;

public sealed record TeamDocumentReadyEvent(
    Guid MessageId,
    string MarkdownContent,
    string SuggestedRelativePath) : ChatRuntimeEvent;
