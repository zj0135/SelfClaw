namespace SelfClaw.Core.Interfaces;

/// <summary>
/// Detects an explicit Skill activation token in plain user text. The detection is syntactic: the
/// owning turn still validates and preloads the Skill through its normal capability resolution.
/// </summary>
public interface ISkillTokenDetector
{
    bool ContainsExplicitActivation(string text);
}
