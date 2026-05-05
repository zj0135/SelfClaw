namespace SelfClaw.Core.Runtime;

public sealed record AgentMcpServerDefinition(
    string Id,
    string DisplayName,
    string Command,
    IReadOnlyList<string> Args,
    IReadOnlyDictionary<string, string> Env)
{
    public string EffectiveDisplayName => string.IsNullOrWhiteSpace(DisplayName)
        ? Id
        : DisplayName;
}
