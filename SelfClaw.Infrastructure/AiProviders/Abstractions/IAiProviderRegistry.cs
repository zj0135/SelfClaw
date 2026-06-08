namespace SelfClaw.Infrastructure.AiProviders.Abstractions;

/// <summary>
/// Resolves the <see cref="IAiProviderAdapter"/> responsible for a given
/// <see cref="AiProviderKind"/>. Implementations index all registered adapters
/// by kind at construction time.
/// </summary>
public interface IAiProviderRegistry
{
    /// <summary>
    /// Returns the adapter registered for <paramref name="providerKind"/>.
    /// Throws when no adapter is registered for the kind.
    /// </summary>
    IAiProviderAdapter GetRequiredAdapter(AiProviderKind providerKind);
}
