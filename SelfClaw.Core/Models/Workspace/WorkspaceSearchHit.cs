namespace SelfClaw.Core.Models;

public sealed record WorkspaceSearchHit(
    string RelativePath,
    int LineNumber,
    string LineText);