using SelfClaw.Infrastructure.Extensions.Abstractions;

namespace SelfClaw.Infrastructure.Extensions;

internal sealed class PluginVersionLeaseManager : IPluginVersionLeaseManager
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);

    public PluginVersionLease Acquire(string installPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installPath);
        var path = Path.GetFullPath(installPath);
        lock (_gate)
        {
            if (!_entries.TryGetValue(path, out var entry))
            {
                entry = new Entry();
                _entries.Add(path, entry);
            }

            if (entry.DrainCount > 0)
            {
                throw new InvalidOperationException("Plugin version is being removed.");
            }

            entry.ReferenceCount++;
        }

        return new PluginVersionLease(() => Release(path));
    }

    public async Task DrainAsync(string installPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installPath);
        await using var drain = await AcquireDrainsAsync([installPath], cancellationToken).ConfigureAwait(false);
    }

    public async Task<PluginVersionDrainLease> AcquireDrainsAsync(
        IReadOnlyList<string> installPaths,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(installPaths);
        var paths = installPaths
            .Select(path =>
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(path);
                return Path.GetFullPath(path);
            })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var entries = new List<(string Path, Entry Entry)>(paths.Length);
        Task[] drained;
        lock (_gate)
        {
            foreach (var path in paths)
            {
                if (!_entries.TryGetValue(path, out var entry))
                {
                    entry = new Entry();
                    _entries.Add(path, entry);
                }

                entry.DrainCount++;
                entries.Add((path, entry));
            }

            drained = entries
                .Where(item => item.Entry.ReferenceCount > 0)
                .Select(item => item.Entry.Drained.Task)
                .ToArray();
        }

        try
        {
            await Task.WhenAll(drained).WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            ReleaseDrains(entries);
            throw;
        }

        return new PluginVersionDrainLease(() => ReleaseDrains(entries));
    }

    private void Release(string path)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(path, out var entry))
            {
                return;
            }

            entry.ReferenceCount--;
            if (entry.ReferenceCount > 0)
            {
                return;
            }

            if (entry.DrainCount > 0)
            {
                entry.Drained.TrySetResult();
                return;
            }

            _entries.Remove(path);
        }
    }

    private void ReleaseDrains(IReadOnlyList<(string Path, Entry Entry)> entries)
    {
        lock (_gate)
        {
            foreach (var item in entries)
            {
                if (!_entries.TryGetValue(item.Path, out var current) || !ReferenceEquals(current, item.Entry))
                {
                    continue;
                }

                current.DrainCount--;
                if (current.DrainCount == 0 && current.ReferenceCount == 0)
                {
                    _entries.Remove(item.Path);
                }
            }
        }
    }

    private sealed class Entry
    {
        public int ReferenceCount { get; set; }
        public int DrainCount { get; set; }
        public TaskCompletionSource Drained { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
