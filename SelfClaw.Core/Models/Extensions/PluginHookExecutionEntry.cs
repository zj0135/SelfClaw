namespace SelfClaw.Core.Models;

public sealed record PluginHookExecutionEntry(
    DateTimeOffset TimestampUtc,
    string PluginId,
    string HookId,
    string Event,
    Guid? TurnId,
    string Outcome,
    double DurationMs,
    int? ExitCode,
    string? Detail,
    string? StderrTail);
