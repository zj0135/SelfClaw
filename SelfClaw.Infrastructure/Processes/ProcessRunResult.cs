namespace SelfClaw.Infrastructure.Processes;

internal sealed record ProcessRunResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    bool OutputTruncated);
