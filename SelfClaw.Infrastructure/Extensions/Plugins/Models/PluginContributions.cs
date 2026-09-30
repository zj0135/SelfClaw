namespace SelfClaw.Infrastructure.Extensions.Plugins.Models;

internal sealed record PluginContributions(
    string? DirectInstructions,
    IReadOnlyList<PluginSkillContribution> Skills,
    IReadOnlyList<PluginMcpServerContribution> McpServers,
    IReadOnlyList<PluginViewContribution> Views,
    IReadOnlyList<PluginHookContribution> Hooks);
