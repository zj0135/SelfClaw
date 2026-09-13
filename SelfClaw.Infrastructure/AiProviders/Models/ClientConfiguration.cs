namespace SelfClaw.Infrastructure.AiProviders.Models;

internal sealed record ClientConfiguration(
    TimeSpan Timeout,
    IReadOnlyDictionary<string, string> ExtraHeaders,
    string Fingerprint);
