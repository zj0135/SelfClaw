using ModelContextProtocol.Client;

namespace SelfClaw.Infrastructure.Extensions.Mcp;

internal sealed class McpClientLease : IAsyncDisposable
{
    private readonly Func<ValueTask> _releaseAsync;
    private int _disposed;

    public McpClientLease(
        IReadOnlyList<McpClientTool> tools,
        Func<ValueTask> releaseAsync)
    {
        Tools = tools;
        _releaseAsync = releaseAsync;
    }

    public IReadOnlyList<McpClientTool> Tools { get; }

    public ValueTask DisposeAsync()
        => Interlocked.Exchange(ref _disposed, 1) == 0
            ? _releaseAsync()
            : ValueTask.CompletedTask;
}
