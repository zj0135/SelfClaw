namespace SelfClaw.Infrastructure.Agents.Direct.Hooks.Models;

internal sealed record HookProcessStart(
    string FileName,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string> Environment);
