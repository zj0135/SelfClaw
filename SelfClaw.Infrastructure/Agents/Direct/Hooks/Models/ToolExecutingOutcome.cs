using Microsoft.Extensions.AI;
using SelfClaw.Core.Runtime;

namespace SelfClaw.Infrastructure.Agents.Direct.Hooks.Models;

internal sealed record ToolExecutingOutcome(
    AIFunctionArguments? EffectiveArguments,
    string? EffectiveArgumentsJson,
    IReadOnlyList<HookSource> ArgumentsModifiedBy,
    IReadOnlyList<HookSource> ApprovalRequiredBy,
    string? ApprovalReason,
    HookSource? BlockedBy,
    string? BlockReason,
    IReadOnlyList<HookFailureNotice> IgnoredFailures)
{
    internal static readonly ToolExecutingOutcome Empty = new(null, null, [], [], null, null, null, []);
}
