using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using SelfClaw.Infrastructure.Extensions.Abstractions;
using SelfClaw.Infrastructure.Extensions.Mcp.Models;

namespace SelfClaw.Infrastructure.Extensions.Mcp;

internal sealed class SdkMcpClientConnectionFactory : IMcpClientConnectionFactory
{
    private readonly McpTransportFactory _transportFactory;
    private readonly ILoggerFactory _loggerFactory;

    public SdkMcpClientConnectionFactory(
        McpTransportFactory transportFactory,
        ILoggerFactory? loggerFactory = null)
    {
        _transportFactory = transportFactory;
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
    }

    public async Task<IMcpClientConnection> ConnectAsync(
        ResolvedMcpServerConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var diagnostics = new BoundedDiagnosticBuffer();
        var transport = _transportFactory.Create(configuration, diagnostics);
        var client = await McpClient.CreateAsync(
                transport,
                loggerFactory: _loggerFactory,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        try
        {
            var tools = await client.ListToolsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            return new SdkMcpClientConnection(client, tools.ToArray(), diagnostics);
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
