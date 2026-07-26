using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using SelfClaw.Desktop.Pet;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SelfClaw.Tests.Desktop.Pet;

public sealed class PetPackageCatalogTests
{
    [Fact]
    public void GetBuiltInPackages_uses_one_manifest_contract_and_skips_invalid_identity()
    {
        using var root = new TemporaryPetRoot();
        CreatePackage(
            root.Path,
            "valid-pet",
            new
            {
                id = "valid-pet",
                displayName = "Valid Pet",
                description = "Loaded by the desktop catalog.",
                spritesheetPath = "art/pet.webp",
                author = "SelfClaw",
                grid = CreateOneCellGrid(columns: 2, rows: 3),
            });
        CreatePackage(root.Path, "wrong-folder", new { id = "different-id" });
        var catalog = CreateCatalog(root.Path, _ => CreateBitmap());

        var packages = catalog.GetBuiltInPackages();

        packages.Should().ContainSingle();
        packages[0].Should().Match<PetPackageSummary>(package =>
            package.Id == "valid-pet" &&
            package.DisplayName == "Valid Pet" &&
            package.PreviewAssetPath == "pets/valid-pet/art/pet.webp" &&
            package.Columns == 2 &&
            package.Rows == 3);
    }

    [Fact]
    public void Load_rejects_a_manifest_path_escape_and_falls_back_to_the_default_package()
    {
        using var root = new TemporaryPetRoot();
        CreatePackage(root.Path, PetPackageCatalog.DefaultBuiltInPetId, CreateManifest(PetPackageCatalog.DefaultBuiltInPetId));
        var customDirectory = System.IO.Path.Combine(root.Path, "custom");
        Directory.CreateDirectory(customDirectory);
        WriteManifest(customDirectory, new
        {
            id = "custom",
            spritesheetPath = "../outside.webp",
            grid = CreateOneCellGrid(),
        });
        var loadedPaths = new List<string>();
        var catalog = CreateCatalog(root.Path, path =>
        {
            loadedPaths.Add(path);
            return CreateBitmap();
        });

        var result = catalog.Load(new PetSettings { SpriteSheetPath = customDirectory });

        result.PackageId.Should().Be(PetPackageCatalog.DefaultBuiltInPetId);
        result.Warning.Should().Contain("escapes the package directory");
        loadedPaths.Should().ContainSingle(path => path.Contains(PetPackageCatalog.DefaultBuiltInPetId));
    }

    [Fact]
    public void Load_falls_back_when_the_selected_package_cannot_be_decoded()
    {
        using var root = new TemporaryPetRoot();
        CreatePackage(root.Path, PetPackageCatalog.DefaultBuiltInPetId, CreateManifest(PetPackageCatalog.DefaultBuiltInPetId));
        CreatePackage(root.Path, "broken", CreateManifest("broken"));
        var catalog = CreateCatalog(root.Path, path =>
        {
            if (path.Contains($"{System.IO.Path.DirectorySeparatorChar}broken{System.IO.Path.DirectorySeparatorChar}"))
            {
                throw new InvalidOperationException("decoder rejected selected package");
            }

            return CreateBitmap();
        });

        var result = catalog.Load(new PetSettings { SpriteSheetPath = "broken" });

        result.PackageId.Should().Be(PetPackageCatalog.DefaultBuiltInPetId);
        result.Warning.Should().Be("decoder rejected selected package");
        result.SpriteSheet.RowIds.Should().ContainSingle().Which.Should().Be(PetLayout.IdleRowId);
    }

    [Fact]
    public void Load_uses_manifest_sprite_and_grid_before_the_settings_override()
    {
        using var root = new TemporaryPetRoot();
        CreatePackage(
            root.Path,
            "manifest-pet",
            new
            {
                id = "manifest-pet",
                spritesheetPath = "art/pet.webp",
                grid = CreateOneCellGrid(columns: 2, rows: 1),
            });
        var loadedPaths = new List<string>();
        var catalog = CreateCatalog(root.Path, path =>
        {
            loadedPaths.Add(path);
            return CreateBitmap(width: 2, height: 1);
        });
        var settingsGrid = new GridConfig
        {
            Cols = 1,
            Rows = 1,
            CellWidth = 2,
            CellHeight = 1,
            RowsDef = [new RowDef { Id = PetLayout.IdleRowId, Frames = 1, Fps = 1 }],
        };

        var result = catalog.Load(new PetSettings
        {
            SpriteSheetPath = "manifest-pet",
            Grid = settingsGrid,
        });

        result.PackageId.Should().Be("manifest-pet");
        result.SpriteSheet.CellWidth.Should().Be(1);
        loadedPaths.Should().ContainSingle().Which.Should().Be(
            System.IO.Path.Combine(root.Path, "manifest-pet", "art", "pet.webp"));
    }

    [Fact]
    public void ResolveSelectedBuiltInPetId_normalizes_legacy_ids_in_the_catalog()
    {
        using var root = new TemporaryPetRoot();
        CreatePackage(root.Path, PetPackageCatalog.DefaultBuiltInPetId, CreateManifest(PetPackageCatalog.DefaultBuiltInPetId));
        CreatePackage(root.Path, "clippit", CreateManifest("clippit"));
        var catalog = CreateCatalog(root.Path, _ => CreateBitmap());

        catalog.ResolveSelectedBuiltInPetId("clippy").Should().Be("clippit");
        catalog.ResolveSelectedBuiltInPetId("missing").Should().Be(PetPackageCatalog.DefaultBuiltInPetId);
    }

    private static PetPackageCatalog CreateCatalog(
        string root,
        Func<string, BitmapSource> decoder)
        => new(
            root,
            new FakePetSpriteDecoder(decoder),
            NullLogger<PetPackageCatalog>.Instance);

    private static object CreateManifest(string id)
        => new
        {
            id,
            spritesheetPath = "spritesheet.webp",
            grid = CreateOneCellGrid(),
        };

    private static object CreateOneCellGrid(int columns = 1, int rows = 1)
        => new
        {
            cols = columns,
            rows,
            cellWidth = 1,
            cellHeight = 1,
            rowsDef = new[]
            {
                new { id = PetLayout.IdleRowId, frames = 1, fps = 1 },
            },
        };

    private static void CreatePackage(string root, string id, object manifest)
    {
        var packageDirectory = System.IO.Path.Combine(root, id);
        Directory.CreateDirectory(packageDirectory);
        var spriteSheetPath = GetSpriteSheetPath(manifest);
        var fullSpriteSheetPath = System.IO.Path.Combine(packageDirectory, spriteSheetPath);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(fullSpriteSheetPath)!);
        File.WriteAllBytes(fullSpriteSheetPath, [0x01]);
        WriteManifest(packageDirectory, manifest);
    }

    private static string GetSpriteSheetPath(object manifest)
    {
        var json = JsonSerializer.SerializeToElement(manifest);
        return json.TryGetProperty("spritesheetPath", out var path)
            ? path.GetString() ?? "spritesheet.webp"
            : "spritesheet.webp";
    }

    private static void WriteManifest(string packageDirectory, object manifest)
    {
        File.WriteAllText(
            System.IO.Path.Combine(packageDirectory, "pet.json"),
            JsonSerializer.Serialize(manifest));
    }

    private static BitmapSource CreateBitmap(int width = 1, int height = 1)
    {
        var bitmap = BitmapSource.Create(
            width,
            height,
            96,
            96,
            PixelFormats.Bgra32,
            palette: null,
            pixels: new byte[width * height * 4],
            stride: width * 4);
        bitmap.Freeze();
        return bitmap;
    }
}
