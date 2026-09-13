namespace SelfClaw.Infrastructure.Options;

public sealed record StoragePaths(
    string AppDataDirectory,
    string DatabasePath,
    string SecretsDirectory,
    string LogsDirectory,
    string WorktreesDirectory);
