using SelfClaw.Infrastructure.Extensions.Mcp.Models;

namespace SelfClaw.Infrastructure.Extensions.Abstractions;

internal interface IMcpClientConnectionFactory
{
    Task<IMcpClientConnection> ConnectAsync(
        ResolvedMcpServerConfiguration configuration,
        CancellationToken cancellationToken = default);
}
