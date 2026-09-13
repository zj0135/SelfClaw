using SelfClaw.Core.Models;
using SelfClaw.Infrastructure.AiProviders.Abstractions;
using SelfClaw.Infrastructure.AiProviders.Models;

namespace SelfClaw.Infrastructure.AiProviders;

/// <summary>
/// Indexes registered <see cref="IAiProviderAdapter"/> instances by <see cref="AiProviderKind"/>.
/// Registering two adapters for the same kind fails fast at construction so the
/// problem surfaces at application startup rather than at request time.
/// </summary>
internal sealed class AiProviderRegistry
{
    private readonly IReadOnlyDictionary<AiProviderKind, IAiProviderAdapter> _adaptersByKind;

    public AiProviderRegistry(IEnumerable<IAiProviderAdapter> adapters)
    {
        ArgumentNullException.ThrowIfNull(adapters);
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

    public IAiProviderAdapter? TryGetAdapter(AiProviderKind providerKind)
        => _adaptersByKind.GetValueOrDefault(providerKind);
}
