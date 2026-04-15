namespace SelfClaw.Core.Models;

public sealed record WorkspaceFileWriteResult(
    string RelativePath,
    bool Applied,
    bool OverwroteExisting,
    int CharacterCount,
    string Message);
