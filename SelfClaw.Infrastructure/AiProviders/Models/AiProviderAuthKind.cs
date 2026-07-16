namespace SelfClaw.Infrastructure.AiProviders.Models;

/// <summary>
/// Authentication scheme used by a provider connection. v1 only supports an
/// API key or no authentication for local providers.
/// </summary>
public enum AiProviderAuthKind
{
    ApiKey = 0,
    None = 1
}
