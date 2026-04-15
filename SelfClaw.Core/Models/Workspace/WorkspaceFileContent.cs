namespace SelfClaw.Core.Models;

public sealed record WorkspaceFileContent(
    string RelativePath,
    string Content,
    bool Truncated);