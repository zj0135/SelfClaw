namespace SelfClaw.Core.Models;

public sealed record GitChangedFile(
    string Path,
    string? OldPath,
    GitChangeKind Kind,
    int? AddedLines,
    int? RemovedLines,
    bool IsBinary);
