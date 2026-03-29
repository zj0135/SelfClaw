namespace SelfClaw.Core.Runtime;

public sealed record ChatRuntimeCompletedEvent(
    string FinalMarkdown,
    int? InputTokens,
    int? OutputTokens,
    TimeSpan Duration) : ChatRuntimeEvent;