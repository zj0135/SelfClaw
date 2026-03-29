namespace SelfClaw.Core.Runtime;

public sealed record AssistantDeltaEvent(string DeltaMarkdown) : ChatRuntimeEvent;