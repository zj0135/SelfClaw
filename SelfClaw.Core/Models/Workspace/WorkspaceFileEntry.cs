namespace SelfClaw.Core.Models;

public sealed record WorkspaceFileEntry(
    string RelativePath,
    bool IsDirectory,
    long? SizeBytes);