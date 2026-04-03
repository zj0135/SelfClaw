namespace SelfClaw.Core.Runtime;

public sealed record AssistantDeltaEvent(
    Guid MessageId,
    string DeltaMarkdown) : ChatRuntimeEvent;
