namespace SelfClaw.Infrastructure.Agents.Cli.Session.Models;

/// <summary>
/// The resume / new-session ids resolved for a single turn, ready to drop into
/// <c>CliRunContext</c>. Produced by <see cref="CliSessionResolver.PrepareAsync"/>.
/// </summary>
public sealed record CliSessionPlan(
    string? ResumeSessionId,
    string? NewSessionId);
