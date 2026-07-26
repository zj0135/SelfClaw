namespace SelfClaw.Infrastructure.Extensions.Skills.Models;

internal sealed record SkillToken(
    string RawText,
    string Id,
    bool IsValid,
    int Index,
    int Length);
