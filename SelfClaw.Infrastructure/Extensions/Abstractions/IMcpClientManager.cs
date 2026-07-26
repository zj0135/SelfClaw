using SelfClaw.Core.Models;
using SelfClaw.Infrastructure.Extensions.Mcp;
using SelfClaw.Infrastructure.Extensions.Mcp.Models;

namespace SelfClaw.Infrastructure.Extensions.Abstractions;

internal interface IMcpClientManager
{
    Task<McpClientLease> AcquireAsync(
        ResolvedMcpServerConfiguration configuration,
        CancellationToken cancellationToken = default);

    Task<McpHealthResult> TestAsync(
        ResolvedMcpServerConfiguration configuration,
        CancellationToken cancellationToken = default);

    Task DrainAsync(string serverId, CancellationToken cancellationToken = default);
}
