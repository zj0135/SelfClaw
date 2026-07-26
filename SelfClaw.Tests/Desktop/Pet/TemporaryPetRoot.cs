namespace SelfClaw.Tests.Desktop.Pet;

internal sealed class TemporaryPetRoot : IDisposable
{
    private readonly string _basePath;

    public TemporaryPetRoot()
    {
        _basePath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "selfclaw-pet-tests");
        Path = System.IO.Path.Combine(_basePath, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        if (!Directory.Exists(Path))
        {
            return;
        }

        var resolved = System.IO.Path.GetFullPath(Path);
        var resolvedBase = System.IO.Path.GetFullPath(_basePath) + System.IO.Path.DirectorySeparatorChar;
        if (!resolved.StartsWith(resolvedBase, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Refusing to delete unexpected test directory: {resolved}");
        }

        Directory.Delete(resolved, recursive: true);
    }
}
