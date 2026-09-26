using SelfClaw.Core.Runtime;

namespace SelfClaw.Infrastructure.Agents.Direct.Hooks.Models;

internal sealed record ToolExecutedOutcome(
    IReadOnlyList<HookFeedback> Feedback,
    IReadOnlyList<HookFailureNotice> IgnoredFailures)
{
    internal static readonly ToolExecutedOutcome Empty = new([], []);
}
