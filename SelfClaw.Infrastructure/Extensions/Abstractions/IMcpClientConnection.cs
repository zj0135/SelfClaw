using ModelContextProtocol.Client;

namespace SelfClaw.Infrastructure.Extensions.Abstractions;

internal interface IMcpClientConnection : IAsyncDisposable
{
    IReadOnlyList<McpClientTool> Tools { get; }

    Task PingAsync(CancellationToken cancellationToken = default);
}
