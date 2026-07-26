using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace SelfClaw.Desktop.Pet;

public sealed class PetPackageCatalog
{
    internal const string DefaultBuiltInPetId = "yorha-sit-2b";

    private static readonly IReadOnlyDictionary<string, string> LegacyPetIds =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["yorha-si"] = DefaultBuiltInPetId,
            ["clippy"] = "clippit",
        };

    private static readonly IReadOnlyDictionary<string, int> BuiltInOrder =
        new[]
        {
            DefaultBuiltInPetId,
            "yelling-dario",
            "tux",
            "slavik",
            "nyako-shigure",
            "dentist",
            "dario",
            "clippit",
        }
        .Select((id, index) => (id, index))
        .ToDictionary(item => item.id, item => item.index, StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _builtInPetsRoot;
    private readonly IPetSpriteDecoder _spriteDecoder;
    private readonly ILogger<PetPackageCatalog> _logger;

    public PetPackageCatalog(ILogger<PetPackageCatalog> logger)
        : this(
            Path.Combine(AppContext.BaseDirectory, "Assets", "pets"),
            new WebpSpriteLoader(),
            logger)
    {
    }

    internal PetPackageCatalog(
        string builtInPetsRoot,
        IPetSpriteDecoder spriteDecoder,
        ILogger<PetPackageCatalog> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(builtInPetsRoot);
        ArgumentNullException.ThrowIfNull(spriteDecoder);
        ArgumentNullException.ThrowIfNull(logger);

        _builtInPetsRoot = Path.GetFullPath(builtInPetsRoot);
        _spriteDecoder = spriteDecoder;
        _logger = logger;
    }

    internal IReadOnlyList<PetPackageSummary> GetBuiltInPackages()
    {
        if (!Directory.Exists(_builtInPetsRoot))
        {
            return [];
        }

        var packages = new List<PetPackageSummary>();
        foreach (var packageDirectory in Directory.EnumerateDirectories(_builtInPetsRoot))
        {
            try
            {
                packages.Add(CreateBuiltInSummary(packageDirectory));
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Ignoring invalid built-in pet package at {PackageDirectory}.", packageDirectory);
            }
        }

        return packages
            .OrderBy(package => BuiltInOrder.TryGetValue(package.Id, out var order) ? order : int.MaxValue)
            .ThenBy(package => package.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    internal PetPackageSummary GetBuiltInPackage(string petId)
    {
        var normalizedId = NormalizeBuiltInId(petId);
        var packageDirectory = GetBuiltInPackageDirectory(normalizedId);
        if (!Directory.Exists(packageDirectory))
        {
            throw new FileNotFoundException("Built-in pet package was not found.", packageDirectory);
        }

        return CreateBuiltInSummary(packageDirectory);
    }

    internal string ResolveSelectedBuiltInPetId(string? configuredPath)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            return ResolveAvailableDefaultId();
        }

        var normalized = NormalizeLegacyId(configuredPath.Trim());
        if (IsSafeBuiltInPetId(normalized) && Directory.Exists(GetBuiltInPackageDirectory(normalized)))
        {
            return normalized;
        }

        try
        {
            var fullPath = Path.GetFullPath(configuredPath);
            var relative = Path.GetRelativePath(_builtInPetsRoot, fullPath);
            if (!relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative))
            {
                var id = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
                if (IsSafeBuiltInPetId(id) && Directory.Exists(GetBuiltInPackageDirectory(id)))
                {
                    return id;
                }
            }
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
        }

        return ResolveAvailableDefaultId();
    }

    internal PetLoadedPackage Load(PetSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        try
        {
            return LoadSelection(ResolveSelection(settings), settings.Grid, warning: null);
        }
        catch (Exception exception)
        {
            var defaultSelection = ResolveBuiltInSelection(ResolveAvailableDefaultId());
            if (IsSameSprite(settings.SpriteSheetPath, defaultSelection))
            {
                throw;
            }

            _logger.LogWarning(exception, "Falling back to the default pet package.");
            return LoadSelection(defaultSelection, settings.Grid, exception.Message);
        }
    }

    private PetLoadedPackage LoadSelection(
        PetPackageSelection selection,
        GridConfig? settingsGrid,
        string? warning)
    {
        var bitmap = _spriteDecoder.Load(selection.SpriteSheetPath);
        var grid = selection.Grid ?? settingsGrid ?? PetLayout.CreateDefaultGrid();
        var spriteSheet = SpriteSheet.Create(bitmap, grid);
        return new PetLoadedPackage(selection.PackageId, spriteSheet, warning);
    }

    private PetPackageSelection ResolveSelection(PetSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.SpriteSheetPath))
        {
            return ResolveBuiltInSelection(ResolveAvailableDefaultId());
        }

        var configuredPath = settings.SpriteSheetPath.Trim();
        var normalizedId = NormalizeLegacyId(configuredPath);
        if (IsSafeBuiltInPetId(normalizedId))
        {
            return ResolveBuiltInSelection(normalizedId);
        }

        var fullPath = ResolveConfiguredPath(configuredPath);
        return Directory.Exists(fullPath)
            ? ResolvePackageDirectory(fullPath, packageId: null)
            : new PetPackageSelection(null, fullPath, Grid: null);
    }

    private PetPackageSelection ResolveBuiltInSelection(string petId)
    {
        var normalizedId = NormalizeBuiltInId(petId);
        var packageDirectory = GetBuiltInPackageDirectory(normalizedId);
        if (!Directory.Exists(packageDirectory))
        {
            throw new FileNotFoundException("Built-in pet package was not found.", packageDirectory);
        }

        return ResolvePackageDirectory(packageDirectory, normalizedId);
    }

    private PetPackageSelection ResolvePackageDirectory(string packageDirectory, string? packageId)
    {
        var manifest = ReadManifest(packageDirectory);
        ValidateManifestId(manifest, packageId ?? Path.GetFileName(packageDirectory));
        var spriteSheetPath = ResolvePackageFile(
            packageDirectory,
            string.IsNullOrWhiteSpace(manifest?.SpritesheetPath) ? "spritesheet.webp" : manifest.SpritesheetPath);
        return new PetPackageSelection(packageId, spriteSheetPath, manifest?.Grid);
    }

    private PetPackageSummary CreateBuiltInSummary(string packageDirectory)
    {
        var packageId = Path.GetFileName(Path.TrimEndingDirectorySeparator(packageDirectory));
        if (!IsSafeBuiltInPetId(packageId))
        {
            throw new InvalidOperationException($"Pet package directory '{packageId}' is not a valid id.");
        }

        var manifest = ReadManifest(packageDirectory);
        ValidateManifestId(manifest, packageId);
        var spriteSheetPath = ResolvePackageFile(
            packageDirectory,
            string.IsNullOrWhiteSpace(manifest?.SpritesheetPath) ? "spritesheet.webp" : manifest.SpritesheetPath);
        if (!File.Exists(spriteSheetPath))
        {
            throw new FileNotFoundException("Pet package spritesheet was not found.", spriteSheetPath);
        }

        var grid = manifest?.Grid ?? PetLayout.CreateDefaultGrid();
        var defaultGrid = PetLayout.CreateDefaultGrid();
        return new PetPackageSummary(
            packageId,
            NormalizeText(manifest?.DisplayName) ?? packageId,
            NormalizeText(manifest?.Description) ?? "内置桌面宠物包。",
            NormalizeText(manifest?.Author),
            manifest?.Tags.Where(tag => !string.IsNullOrWhiteSpace(tag)).Select(tag => tag.Trim()).ToArray() ?? [],
            NormalizeText(manifest?.Source),
            NormalizeText(manifest?.SourceUrl),
            CreatePreviewAssetPath(spriteSheetPath),
            grid.Cols > 0 ? grid.Cols : defaultGrid.Cols,
            grid.Rows > 0 ? grid.Rows : defaultGrid.Rows);
    }

    private PetPackageManifest? ReadManifest(string packageDirectory)
    {
        var manifestPath = Path.Combine(packageDirectory, "pet.json");
        if (!File.Exists(manifestPath))
        {
            return null;
        }

        var manifest = JsonSerializer.Deserialize<PetPackageManifest>(File.ReadAllText(manifestPath), JsonOptions);
        return manifest ?? throw new InvalidOperationException($"Pet package manifest '{manifestPath}' is empty.");
    }

    private static void ValidateManifestId(PetPackageManifest? manifest, string packageId)
    {
        if (!string.IsNullOrWhiteSpace(manifest?.Id) &&
            !string.Equals(manifest.Id.Trim(), packageId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Pet package manifest id '{manifest.Id}' does not match directory id '{packageId}'.");
        }
    }

    private string ResolveAvailableDefaultId()
    {
        var preferred = GetBuiltInPackageDirectory(DefaultBuiltInPetId);
        if (Directory.Exists(preferred))
        {
            return DefaultBuiltInPetId;
        }

        var first = GetBuiltInPackages().FirstOrDefault();
        return first?.Id ?? DefaultBuiltInPetId;
    }

    private string GetBuiltInPackageDirectory(string petId)
        => Path.Combine(_builtInPetsRoot, petId);

    private static string NormalizeBuiltInId(string petId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(petId);
        var normalizedId = NormalizeLegacyId(petId.Trim());
        if (!IsSafeBuiltInPetId(normalizedId))
        {
            throw new ArgumentException("Pet id is invalid.", nameof(petId));
        }

        return normalizedId;
    }

    private static string NormalizeLegacyId(string petId)
        => LegacyPetIds.TryGetValue(petId, out var normalized) ? normalized : petId;

    private static bool IsSafeBuiltInPetId(string? petId)
        => !string.IsNullOrWhiteSpace(petId) &&
           petId.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    private static string ResolveConfiguredPath(string configuredPath)
    {
        if (Path.IsPathFullyQualified(configuredPath))
        {
            return Path.GetFullPath(configuredPath);
        }

        var appRelative = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, configuredPath));
        return Directory.Exists(appRelative) || File.Exists(appRelative)
            ? appRelative
            : Path.GetFullPath(configuredPath);
    }

    private static string ResolvePackageFile(string packageDirectory, string relativePath)
    {
        if (Path.IsPathFullyQualified(relativePath))
        {
            throw new InvalidOperationException("Pet package spritesheet path must be relative to the package directory.");
        }

        var root = Path.GetFullPath(packageDirectory);
        var fullPath = Path.GetFullPath(Path.Combine(root, relativePath));
        var relative = Path.GetRelativePath(root, fullPath);
        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
        {
            throw new InvalidOperationException("Pet package spritesheet path escapes the package directory.");
        }

        return fullPath;
    }

    private string CreatePreviewAssetPath(string spriteSheetPath)
    {
        var relative = Path.GetRelativePath(_builtInPetsRoot, spriteSheetPath);
        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
        {
            throw new InvalidOperationException("Built-in pet preview is outside the pet asset root.");
        }

        return $"pets/{relative.Replace(Path.DirectorySeparatorChar, '/')}";
    }

    private static bool IsSameSprite(string? configuredPath, PetPackageSelection defaultSelection)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            return true;
        }

        return string.Equals(
            NormalizeLegacyId(configuredPath.Trim()),
            defaultSelection.PackageId,
            StringComparison.OrdinalIgnoreCase);
    }

    private static string? NormalizeText(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
