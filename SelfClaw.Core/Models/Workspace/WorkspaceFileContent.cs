namespace SelfClaw.Core.Models;

public sealed record WorkspaceFileContent(
    string RelativePath,
    string Content,
    bool Truncated,
    int StartLine = 1,
    int EndLine = 0,
    int TotalLines = 0);
