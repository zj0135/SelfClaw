using SelfClaw.Infrastructure.Extensions.Skills.Models;

namespace SelfClaw.Infrastructure.Extensions.Runtime.Models;

internal sealed record PluginCapabilities(
    IReadOnlyList<string> Instructions,
    IReadOnlyDictionary<string, ResolvedSkill> Skills,
    IReadOnlyDictionary<string, string> PluginRoots)
{
    public static PluginCapabilities Empty { get; } = new(
        [],
        new Dictionary<string, ResolvedSkill>(),
        new Dictionary<string, string>());
}
