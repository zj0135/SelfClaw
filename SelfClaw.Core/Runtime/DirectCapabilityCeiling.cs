namespace SelfClaw.Core.Runtime;

public sealed record DirectCapabilityCeiling(
    string ToolPolicy,
    IReadOnlyList<DirectExtensionCapability> Plugins,
    IReadOnlyList<DirectExtensionCapability> Skills,
    IReadOnlyList<DirectMcpCapability> McpServers,
    IReadOnlyList<string> SubagentIds);
