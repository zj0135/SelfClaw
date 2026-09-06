using SelfClaw.Core.Runtime;

namespace SelfClaw.Infrastructure.Extensions.Runtime.Models;

/// <summary>
/// The MCP servers this turn connected to. Their leases are owned by the turn's lease scope, not by
/// this snapshot.
/// </summary>
internal sealed record McpCapabilities(
    IReadOnlyList<DirectMcpCapability> Capabilities);
