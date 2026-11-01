namespace SelfClaw.Core.Models;

public sealed record GitFileDiff(
    string Path,
    string? OldPath,
    GitChangeKind Kind,
    bool IsBinary,
    string? Patch,
    bool Truncated,
    int AddedLines,
    int RemovedLines);
