namespace SelfClaw.Tests.TestDoubles;

internal static class TestRepositoryRoot
{
    internal static string GetPath(params string[] parts)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "SelfClaw.slnx")))
                return Path.Combine([directory.FullName, .. parts]);
        }
        throw new DirectoryNotFoundException("The test output has no SelfClaw repository ancestor.");
    }
}
