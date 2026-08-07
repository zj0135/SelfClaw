using Microsoft.Extensions.AI;

namespace SelfClaw.Infrastructure.Extensions.Runtime.Models;

internal sealed record SkillCapabilities(
    IReadOnlyList<string> Instructions,
    IReadOnlyList<AITool> Tools,
    IReadOnlyList<DirectToolDescriptor> Descriptors,
    IReadOnlyDictionary<Guid, string> MessageAdjustments,
    IReadOnlyList<string> ResolvedSkillIds);
