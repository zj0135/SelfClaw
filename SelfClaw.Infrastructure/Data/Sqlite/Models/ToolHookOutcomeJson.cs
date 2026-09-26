namespace SelfClaw.Infrastructure.Data.Sqlite.Models;

internal sealed record ToolHookOutcomeJson(
    IReadOnlyList<HookSourceJson> ArgumentsModifiedBy,
    IReadOnlyList<HookSourceJson> ApprovalRequiredBy,
    HookSourceJson? BlockedBy,
    string? BlockReason,
    IReadOnlyList<HookFailureJson> IgnoredFailures);
