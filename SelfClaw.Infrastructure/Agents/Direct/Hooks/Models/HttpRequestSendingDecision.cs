namespace SelfClaw.Infrastructure.Agents.Direct.Hooks.Models;

internal sealed record HttpRequestSendingDecision(IReadOnlyDictionary<string, string>? AddHeaders);
