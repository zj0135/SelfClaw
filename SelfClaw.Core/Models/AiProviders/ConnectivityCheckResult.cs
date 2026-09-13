namespace SelfClaw.Core.Models;

public sealed record ConnectivityCheckResult(bool Ok, long LatencyMs, string? ErrorMessage);
