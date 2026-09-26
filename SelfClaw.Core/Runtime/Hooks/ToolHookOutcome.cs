namespace SelfClaw.Core.Runtime;

public sealed record ToolHookOutcome(
    string? EffectiveArgumentsJson,
    IReadOnlyList<HookSource> ArgumentsModifiedBy,
    IReadOnlyList<HookSource> ApprovalRequiredBy,
    HookSource? BlockedBy,
    string? BlockReason,
    IReadOnlyList<HookFeedback> Feedback,
    IReadOnlyList<HookFailureNotice> IgnoredFailures);
