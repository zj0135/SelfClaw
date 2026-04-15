namespace SelfClaw.Core.Models;

public sealed record ProviderProfile(
    Guid Id,
    string Name,
    string Endpoint,
    string Model,
    bool TemperatureEnabled,
    double Temperature,
    bool TopPEnabled,
    double TopP,
    ApiStyle ApiStyle,
    string SecretRef,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);
