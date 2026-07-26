using Microsoft.Extensions.AI;
using SelfClaw.Infrastructure.Extensions.Runtime.Models;

namespace SelfClaw.Infrastructure.Extensions.Runtime;

internal sealed class DirectTurnCapabilityLease : IAsyncDisposable
{
    private readonly Func<ValueTask>? _disposeAsync;
    private int _disposed;

    public DirectTurnCapabilityLease(
        IReadOnlyList<string> systemInstructions,
        IReadOnlyList<AITool> tools,
        IReadOnlyDictionary<string, DirectToolDescriptor> toolDescriptors,
        IReadOnlyDictionary<Guid, string> messageAdjustments,
        IReadOnlyList<string> diagnostics,
        Func<ValueTask>? disposeAsync = null)
    {
        SystemInstructions = systemInstructions;
        Tools = tools;
        ToolDescriptors = toolDescriptors;
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
