using SelfClaw.Infrastructure.AiProviders.Abstractions;

namespace SelfClaw.Infrastructure.AiProviders;

/// <summary>
/// Default <see cref="IAiProviderRegistry"/>. Collects every registered
/// <see cref="IAiProviderAdapter"/> and indexes them by <see cref="AiProviderKind"/>.
/// Registering two adapters for the same kind fails fast at construction so the
/// problem surfaces at application startup rather than at request time.
/// </summary>
internal sealed class AiProviderRegistry : IAiProviderRegistry
{
    private readonly IReadOnlyDictionary<AiProviderKind, IAiProviderAdapter> _adaptersByKind;

    public AiProviderRegistry(IEnumerable<IAiProviderAdapter> adapters)
    {
        var map = new Dictionary<AiProviderKind, IAiProviderAdapter>();

        foreach (var adapter in adapters)
        {
            if (!map.TryAdd(adapter.ProviderKind, adapter))
            {
                var existing = map[adapter.ProviderKind];
                throw new InvalidOperationException(
                    $"Duplicate AI provider adapter for kind '{adapter.ProviderKind}': " +
                    $"'{existing.GetType().Name}' and '{adapter.GetType().Name}' both registered.");
            }
        }

        _adaptersByKind = map;
    }

    public IAiProviderAdapter GetRequiredAdapter(AiProviderKind providerKind)
    {
        if (_adaptersByKind.TryGetValue(providerKind, out var adapter))
        {
            return adapter;
        }

        throw new KeyNotFoundException(
            $"No AI provider adapter is registered for provider kind '{providerKind}'.");
    }
}
