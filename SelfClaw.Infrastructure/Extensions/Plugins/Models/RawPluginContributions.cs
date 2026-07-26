namespace SelfClaw.Infrastructure.Extensions.Plugins.Models;

internal sealed record RawPluginContributions(
    string? DirectInstructions = null,
    IReadOnlyList<RawPluginSkillContribution>? Skills = null,
    IReadOnlyList<RawPluginMcpServerContribution>? McpServers = null);
