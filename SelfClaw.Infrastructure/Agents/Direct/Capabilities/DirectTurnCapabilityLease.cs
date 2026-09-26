using SelfClaw.Infrastructure.Agents.Direct.Hooks.Models;
using SelfClaw.Infrastructure.Agents.Direct.Tools.Models;
using Microsoft.Extensions.AI;

namespace SelfClaw.Infrastructure.Agents.Direct.Capabilities;

internal sealed class DirectTurnCapabilityLease : IAsyncDisposable
{
    private readonly Func<ValueTask>? _disposeAsync;
    private int _disposed;

    public DirectTurnCapabilityLease(
        IReadOnlyList<string> systemInstructions,
        IReadOnlyList<DirectToolBinding> tools,
        IReadOnlyDictionary<Guid, string> messageAdjustments,
        IReadOnlyList<string> diagnostics,
        Func<ValueTask>? disposeAsync = null,
        IReadOnlyList<ResolvedPluginHook>? hooks = null,
        IReadOnlyList<string>? hookNotices = null,
        string? hookBlockReason = null)
    {
        SystemInstructions = systemInstructions;
        Tools = tools.Select(binding => (AITool)binding.Tool).ToArray();
        Bindings = tools.ToDictionary(binding => binding.Tool.Name, StringComparer.Ordinal);
        MessageAdjustments = messageAdjustments;
        Diagnostics = diagnostics;
        Hooks = hooks ?? [];
        HookNotices = hookNotices ?? [];
        HookBlockReason = hookBlockReason;
        _disposeAsync = disposeAsync;
    }

    public IReadOnlyList<string> SystemInstructions { get; }
    public IReadOnlyList<AITool> Tools { get; }
    public IReadOnlyDictionary<string, DirectToolBinding> Bindings { get; }
    public IReadOnlyDictionary<Guid, string> MessageAdjustments { get; }
    public IReadOnlyList<string> Diagnostics { get; }
    public IReadOnlyList<ResolvedPluginHook> Hooks { get; }
    public IReadOnlyList<string> HookNotices { get; }
    public string? HookBlockReason { get; }

    public ValueTask DisposeAsync()
        => Interlocked.Exchange(ref _disposed, 1) == 0 && _disposeAsync is not null
            ? _disposeAsync()
            : ValueTask.CompletedTask;
}
