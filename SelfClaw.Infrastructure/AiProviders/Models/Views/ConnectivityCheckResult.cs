namespace SelfClaw.Infrastructure.AiProviders.Models.Views;

public sealed record ConnectivityCheckResult(bool Ok, long LatencyMs, string? ErrorMessage);
