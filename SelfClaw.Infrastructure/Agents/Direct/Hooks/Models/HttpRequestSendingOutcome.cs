namespace SelfClaw.Infrastructure.Agents.Direct.Hooks.Models;

internal sealed record HttpRequestSendingOutcome(IReadOnlyList<KeyValuePair<string, string>> Headers)
{
    internal static readonly HttpRequestSendingOutcome Empty = new([]);
}
