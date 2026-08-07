namespace SelfClaw.Core.Models;

public sealed record SubagentTiming(
    DateTimeOffset QueuedAtUtc,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    double? DurationMs);
