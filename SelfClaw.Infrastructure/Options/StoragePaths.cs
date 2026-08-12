namespace SelfClaw.Infrastructure.Options;

public sealed record StoragePaths(
    string AppDataDirectory,
    string DatabasePath,
    string SecretsDirectory)
{
    public string LogsDirectory => Path.Combine(AppContext.BaseDirectory, "logs");

    public string WorktreesDirectory => Path.Combine(AppDataDirectory, "worktrees");

    public static StoragePaths CreateDefault()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SelfClaw");

        return new StoragePaths(
            root,
            Path.Combine(root, "selfclaw.db"),
            Path.Combine(root, "secrets"));
    }
}
