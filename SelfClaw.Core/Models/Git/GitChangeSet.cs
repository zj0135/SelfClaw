namespace SelfClaw.Core.Models;

public sealed record GitChangeSet(
    IReadOnlyList<GitChangedFile> Files,
    bool AtFileLimit);
