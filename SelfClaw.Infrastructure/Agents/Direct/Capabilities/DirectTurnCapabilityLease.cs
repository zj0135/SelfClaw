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
        Func<ValueTask>? disposeAsync = null)
    {
        SystemInstructions = systemInstructions;
        Tools = tools.Select(binding => (AITool)binding.Tool).ToArray();
        ToolDescriptors = tools.ToDictionary(binding => binding.Tool.Name, binding => binding.Descriptor, StringComparer.Ordinal);
        MessageAdjustments = messageAdjustments;
        Diagnostics = diagnostics;
        _disposeAsync = disposeAsync;
    }

    public IReadOnlyList<string> SystemInstructions { get; }
    public IReadOnlyList<AITool> Tools { get; }
    public IReadOnlyDictionary<string, DirectToolDescriptor> ToolDescriptors { get; }
    public IReadOnlyDictionary<Guid, string> MessageAdjustments { get; }
    public IReadOnlyList<string> Diagnostics { get; }

    public ValueTask DisposeAsync()
        => Interlocked.Exchange(ref _disposed, 1) == 0 && _disposeAsync is not null
            ? _disposeAsync()
            : ValueTask.CompletedTask;
}
