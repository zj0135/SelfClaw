namespace SelfClaw.Infrastructure.Extensions.Plugins.Models;

internal sealed record PluginContributions(
    string? DirectInstructions,
    IReadOnlyList<PluginSkillContribution> Skills,
    IReadOnlyList<PluginMcpServerContribution> McpServers,
    IReadOnlyList<PluginPanelContribution> Panels);
