namespace SelfClaw.Core.Models;

public sealed record ShellCommandResult(
    string Command,
    bool Executed,
    int? ExitCode,
    string StandardOutput,
    string StandardError,
    bool OutputTruncated,
    string Message);
