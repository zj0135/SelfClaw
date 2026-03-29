namespace SelfClaw.Core.Models;

public sealed record WorkspaceRoot(
    Guid Id,
    string Name,
    string RootPath,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);