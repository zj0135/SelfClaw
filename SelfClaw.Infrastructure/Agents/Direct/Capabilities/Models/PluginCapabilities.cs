using SelfClaw.Infrastructure.Extensions.Skills.Models;

namespace SelfClaw.Infrastructure.Agents.Direct.Capabilities.Models;

internal sealed record PluginCapabilities(
    IReadOnlyList<string> Instructions,
    IReadOnlyDictionary<string, ResolvedSkill> Skills,
    IReadOnlyDictionary<string, string> PluginRoots);
