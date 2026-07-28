using ModelContextProtocol.Client;
using SelfClaw.Infrastructure.Extensions.Abstractions;

namespace SelfClaw.Infrastructure.Extensions.Mcp;

internal sealed class SdkMcpClientConnection : IMcpClientConnection
{
    private readonly McpClient _client;

    public SdkMcpClientConnection(McpClient client, IReadOnlyList<McpClientTool> tools)
    {
        _client = client;
        Tools = tools;
    }

    public IReadOnlyList<McpClientTool> Tools { get; }

    public async Task PingAsync(CancellationToken cancellationToken = default)
    {
        _ = await _client.PingAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync() => _client.DisposeAsync();
}
