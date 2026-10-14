namespace SelfClaw.Core.Models;

/// <summary>
/// The per-attempt run facts resolved immediately before a turn starts: which concrete model, which
/// capability snapshot and which workspace. Each retry records a new one, so the run row always reflects
/// the attempt that is currently executing.
/// </summary>
public sealed record AutomationRunAttempt(
    Guid RunId,
    int Attempt,
    Guid ConversationId,
    Guid ModelProfileId,
    string CapabilitySnapshotJson,
    Guid? WorkspaceRootId,
    string? WorkspacePathSnapshot,
    DateTimeOffset StartedAtUtc);
