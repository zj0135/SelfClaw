namespace SelfClaw.Desktop.Services.Agents.Models;

internal sealed record SubagentEdit(string Id, string Name, string Description, Guid? ModelProfileId,
    string ToolPolicy, int MaxRunSeconds, string Instructions);
