namespace SelfClaw.Infrastructure.Agents.Direct.Tools.Models;

internal sealed record SkillScriptResult(
    string RelativePath,
    bool Executed,
    int? ExitCode,
    string StandardOutput,
    string StandardError,
    bool OutputTruncated,
    string Message);
