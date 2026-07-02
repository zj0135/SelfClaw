using System.IO;

namespace SelfClaw.Desktop.Pet;

internal static class PetAssetPaths
{
    public const string DefaultBuiltInPetId = "yorha-sit-2b";

    public static string BuiltInPetsRoot => Path.Combine(AppContext.BaseDirectory, "Assets", "pets");

    public static string GetBuiltInPackageDirectory(string petId)
        => Path.Combine(BuiltInPetsRoot, petId);

    public static bool IsSafeBuiltInPetId(string? petId)
    {
        if (string.IsNullOrWhiteSpace(petId))
        {
            return false;
        }

        return petId.All(static c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
    }

    public static string ResolveDefaultPackageDirectory()
    {
        var preferred = GetBuiltInPackageDirectory(DefaultBuiltInPetId);
        if (Directory.Exists(preferred))
        {
            return preferred;
        }

        if (!Directory.Exists(BuiltInPetsRoot))
        {
            return preferred;
        }

        return Directory
            .EnumerateDirectories(BuiltInPetsRoot)
            .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault()
            ?? preferred;
    }

    public static string ResolveConfiguredPath(string configuredPath)
    {
        if (IsSafeBuiltInPetId(configuredPath))
        {
            var builtInDirectory = GetBuiltInPackageDirectory(configuredPath);
            if (Directory.Exists(builtInDirectory))
            {
                return builtInDirectory;
            }
        }

        if (Path.IsPathFullyQualified(configuredPath))
        {
            return Path.GetFullPath(configuredPath);
        }

        var appRelative = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, configuredPath));
        if (Directory.Exists(appRelative) || File.Exists(appRelative))
        {
            return appRelative;
        }

        return Path.GetFullPath(configuredPath);
    }

    public static string ResolveSelectedBuiltInPetId(string? configuredPath)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            return DefaultBuiltInPetId;
        }

        if (IsSafeBuiltInPetId(configuredPath))
        {
            return configuredPath;
        }

        var fullPath = Path.GetFullPath(configuredPath);
        var root = Path.GetFullPath(BuiltInPetsRoot);
        var relative = Path.GetRelativePath(root, fullPath);
        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
        {
            return DefaultBuiltInPetId;
        }

        var id = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
        return IsSafeBuiltInPetId(id) ? id : DefaultBuiltInPetId;
    }
}
