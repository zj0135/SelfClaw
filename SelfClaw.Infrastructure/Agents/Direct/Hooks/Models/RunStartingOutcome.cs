using SelfClaw.Core.Runtime;
using SelfClaw.Infrastructure.Agents.Direct.Context.Models;

namespace SelfClaw.Infrastructure.Agents.Direct.Hooks.Models;

internal sealed record RunStartingOutcome(
    HookSource? BlockedBy,
    string? BlockReason,
    IReadOnlyList<HookContextSection> Context,
    IReadOnlyList<string> Notices)
{
    internal static readonly RunStartingOutcome Empty = new(null, null, [], []);
}
