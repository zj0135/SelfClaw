namespace SelfClaw.Infrastructure.AiProviders.Abstractions;

/// <summary>
/// Authentication scheme used by a provider connection. v1 only supports an
/// API key; the enum exists so future schemes can be added without breaking
/// the persisted connection shape.
/// </summary>
public enum AiProviderAuthKind
{
    ApiKey = 0
}
