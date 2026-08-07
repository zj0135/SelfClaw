using SelfClaw.Core.Runtime;
using SelfClaw.Infrastructure.Extensions.Mcp;

namespace SelfClaw.Infrastructure.Extensions.Runtime.Models;

internal sealed record McpCapabilities(
    IReadOnlyList<McpClientLease> Leases,
    IReadOnlyList<DirectMcpCapability> Capabilities);
