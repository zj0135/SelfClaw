namespace SelfClaw.Infrastructure.Options;

public static class StoragePathDefaults
{
    public static StoragePaths CreateDefault()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SelfClaw");
        return Create(root, Path.Combine(root, "selfclaw.db"), Path.Combine(root, "secrets"));
    }

    public static StoragePaths Create(string appDataDirectory, string databasePath, string secretsDirectory)
        => new(appDataDirectory, databasePath, secretsDirectory,
            Path.Combine(AppContext.BaseDirectory, "logs"), Path.Combine(appDataDirectory, "worktrees"));
}
