using System.Collections.Concurrent;
using SelfClaw.Core.Interfaces.Extensions;
using SelfClaw.Core.Models;

namespace SelfClaw.Infrastructure.Agents.Direct.Hooks;

/// <summary>
/// The in-memory hook execution log: a bounded ring per Plugin, exposing newest-first snapshots.
/// Entries carry no payload or stdout content, only a short decision/failure summary.
/// </summary>
internal sealed class PluginHookExecutionLog : IPluginHookExecutionLog
{
    private const int MaximumEntriesPerPlugin = 200;
    private const int MaximumDetailCharacters = 512;
    private const int MaximumStderrTailBytes = 2 * 1024;

    private readonly ConcurrentDictionary<string, Bucket> _buckets = new(StringComparer.Ordinal);

    public void Append(PluginHookExecutionEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var trimmed = entry with
        {
            Detail = Truncate(entry.Detail, MaximumDetailCharacters),
            StderrTail = TruncateStderrTail(entry.StderrTail)
        };
        _buckets.GetOrAdd(entry.PluginId, _ => new Bucket()).Append(trimmed);
    }

    public IReadOnlyList<PluginHookExecutionEntry> GetRecent(string pluginId)
    {
        ArgumentNullException.ThrowIfNull(pluginId);
        return _buckets.TryGetValue(pluginId, out var bucket) ? bucket.Snapshot() : [];
    }

    private static string? Truncate(string? value, int maximumCharacters)
        => value is { Length: > 0 } && value.Length > maximumCharacters
            ? value[..maximumCharacters]
            : value;

    private static string? TruncateStderrTail(string? value)
        => value is null ? null : HookProtocol.Truncate(value, MaximumStderrTailBytes).Text;

    private sealed class Bucket
    {
        private readonly object _sync = new();
        private readonly Queue<PluginHookExecutionEntry> _entries = new();

        public void Append(PluginHookExecutionEntry entry)
        {
            lock (_sync)
            {
                _entries.Enqueue(entry);
                while (_entries.Count > MaximumEntriesPerPlugin)
                {
                    _entries.Dequeue();
                }
            }
        }

        public IReadOnlyList<PluginHookExecutionEntry> Snapshot()
        {
            lock (_sync)
            {
                return _entries.Reverse().ToArray();
            }
        }
    }
}
