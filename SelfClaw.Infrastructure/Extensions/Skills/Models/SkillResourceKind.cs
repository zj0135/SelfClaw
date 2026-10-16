namespace SelfClaw.Infrastructure.Extensions.Skills.Models;

/// <summary>
/// Distinguishes content the model may only read from content it may ask the runtime to execute.
/// The two have different extension tables and different tool contracts.
/// </summary>
internal enum SkillResourceKind
{
    Resource = 0,
    Script = 1
}
