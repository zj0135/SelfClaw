namespace SelfClaw.Core.Models;

public sealed record ProviderProfile(
    Guid Id,
    string Name,
    string Endpoint,
    string Model,
    ApiStyle ApiStyle,
    string SecretRef,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);