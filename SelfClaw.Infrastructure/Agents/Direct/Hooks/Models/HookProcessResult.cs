namespace SelfClaw.Infrastructure.Agents.Direct.Hooks.Models;

internal sealed record HookProcessResult(
    HookProcessExit Exit,
    int? ExitCode,
    string Stdout,
    string StderrTail,
    TimeSpan Duration,
    string? FailureDetail);
