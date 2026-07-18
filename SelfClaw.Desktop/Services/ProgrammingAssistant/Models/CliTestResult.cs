namespace SelfClaw.Desktop.Services.ProgrammingAssistant.Models;

/// <summary>
/// The outcome of a live probe of a single CLI. <see cref="Success"/> is <c>true</c> only when the version
/// command actually ran and produced output; on failure <see cref="Error"/> carries a human-readable reason
/// (missing binary, non-zero exit, timeout) and <see cref="Version"/> is <c>null</c>.
/// </summary>
public sealed record CliTestResult(string CliId, bool Success, string? Version, string? Error);
