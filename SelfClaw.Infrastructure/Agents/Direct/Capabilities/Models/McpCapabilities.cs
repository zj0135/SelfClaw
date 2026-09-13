using SelfClaw.Infrastructure.Agents.Direct.Tools.Models;
using SelfClaw.Core.Runtime;

namespace SelfClaw.Infrastructure.Agents.Direct.Capabilities.Models;

/// <summary>
/// The MCP servers this turn connected to. Their leases are owned by the turn's lease scope, not by
/// this snapshot.
/// </summary>
internal sealed record McpCapabilities(
    IReadOnlyList<DirectMcpCapability> Capabilities,
    IReadOnlyList<DirectToolBinding> Tools);
