using ModelContextProtocol.Client;
using SelfClaw.Infrastructure.Extensions.Abstractions;

namespace SelfClaw.Infrastructure.Extensions.Mcp;

internal sealed class SdkMcpClientConnection : IMcpClientConnection
{
    private readonly McpClient _client;
    private readonly BoundedDiagnosticBuffer _diagnostics;

    public SdkMcpClientConnection(
        McpClient client,
        IReadOnlyList<McpClientTool> tools,
        BoundedDiagnosticBuffer diagnostics)
    {
        _client = client;
        Tools = tools;
        _diagnostics = diagnostics;
    }

    public IReadOnlyList<McpClientTool> Tools { get; }

    public string? Diagnostics => _diagnostics.Read();

    public async Task PingAsync(CancellationToken cancellationToken = default)
    {
        _ = await _client.PingAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync() => _client.DisposeAsync();
}
