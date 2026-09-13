using SelfClaw.Infrastructure.Agents.Direct.Tools.Models;
using Microsoft.Extensions.AI;

namespace SelfClaw.Infrastructure.Agents.Direct.Capabilities.Models;

internal sealed record SkillCapabilities(
    IReadOnlyList<string> Instructions,
    IReadOnlyList<DirectToolBinding> Tools,
    IReadOnlyDictionary<Guid, string> MessageAdjustments,
    IReadOnlyList<string> ResolvedSkillIds);
