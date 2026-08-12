namespace SelfClaw.Core.Models;

public sealed record GitMergeResult(
    bool Succeeded,
    bool HasConflicts,
    string Message,
    GitWorkspaceState State);
