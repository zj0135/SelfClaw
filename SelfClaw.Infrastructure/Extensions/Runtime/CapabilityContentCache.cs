using System.Collections.Concurrent;
using SelfClaw.Core.Models;
using SelfClaw.Infrastructure.Extensions.Plugins.Models;
using SelfClaw.Infrastructure.Extensions.Skills.Models;

namespace SelfClaw.Infrastructure.Extensions.Runtime;

/// <summary>
/// Caches the static package content the Direct capability assembly would otherwise re-read on every
/// turn: parsed Plugin manifests, Plugin instruction bodies, and Skill metadata. Entries are keyed by
/// package identity, version, content hash, and install path. Health and binding changes do not alter
/// package content. Reads that fail are never cached, so a transient I/O error cannot poison later turns.
/// </summary>
internal sealed class CapabilityContentCache : IDisposable
{
    private const int MaximumEntriesPerKind = 128;

    private readonly CancellationTokenSource _shutdown = new();
    private readonly CancellationToken _readCancellation;
    private readonly ConcurrentDictionary<string, Lazy<Task<PluginManifest>>> _manifests = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Lazy<Task<string>>> _instructionBodies = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Lazy<Task<SkillPackageMetadata>>> _skills = new(StringComparer.Ordinal);
    private int _disposed;

    public CapabilityContentCache()
    {
        _readCancellation = _shutdown.Token;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _shutdown.Cancel();
        _shutdown.Dispose();
        Clear();
    }

    public Task<PluginManifest> GetManifestAsync(
        ExtensionPackageRecord package,
        Func<CancellationToken, Task<PluginManifest>> read,
        CancellationToken cancellationToken)
        => GetAsync(_manifests, CreateKey(package, "plugin.json"), read, cancellationToken);

    public Task<string> GetInstructionBodyAsync(
        ExtensionPackageRecord package,
        string instructionsPath,
        Func<CancellationToken, Task<string>> read,
        CancellationToken cancellationToken)
        => GetAsync(_instructionBodies, CreateKey(package, instructionsPath), read, cancellationToken);

    public Task<SkillPackageMetadata> GetSkillMetadataAsync(
        ExtensionPackageRecord package,
        string skillManifestPath,
        Func<CancellationToken, Task<SkillPackageMetadata>> read,
        CancellationToken cancellationToken)
        => GetAsync(_skills, CreateKey(package, skillManifestPath), read, cancellationToken);

    private static string CreateKey(ExtensionPackageRecord package, string relativePath)
        => string.Join('\0', package.Kind, package.Id, package.Version, package.ContentHash, package.InstallPath, relativePath);

    private static void EvictIfNeeded<TKey, TValue>(ConcurrentDictionary<TKey, TValue> cache)
        where TKey : notnull
    {
        if (cache.Count >= MaximumEntriesPerKind)
        {
            cache.Clear();
        }
    }

    private async Task<TValue> GetAsync<TKey, TValue>(
        ConcurrentDictionary<TKey, Lazy<Task<TValue>>> cache,
        TKey key,
        Func<CancellationToken, Task<TValue>> read,
        CancellationToken cancellationToken) where TKey : notnull
    {
        ArgumentNullException.ThrowIfNull(read);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (!cache.TryGetValue(key, out var lazy))
        {
            EvictIfNeeded(cache);
            Lazy<Task<TValue>>? candidate = null;
            candidate = new Lazy<Task<TValue>>(ReadAsync, LazyThreadSafetyMode.ExecutionAndPublication);
            lazy = cache.GetOrAdd(key, candidate);

            async Task<TValue> ReadAsync()
            {
                try
                {
                    return await read(_readCancellation).ConfigureAwait(false);
                }
                catch
                {
                    // Only the failing shared read can evict its entry, even after all waiters cancel.
                    if (candidate is not null)
                    {
                        cache.TryRemove(new KeyValuePair<TKey, Lazy<Task<TValue>>>(key, candidate));
                    }
                    throw;
                }
            }
        }

        return await lazy.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private void Clear()
    {
        _manifests.Clear();
        _instructionBodies.Clear();
        _skills.Clear();
    }
}
