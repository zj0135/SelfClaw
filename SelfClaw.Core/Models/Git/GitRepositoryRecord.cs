namespace SelfClaw.Core.Models;

public sealed record GitRepositoryRecord(
    Guid Id,
    string Name,
    string CommonDirectory,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);
