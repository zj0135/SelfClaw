using System.IO.Compression;
using FluentAssertions;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Infrastructure.Data.Sqlite;
using SelfClaw.Infrastructure.Data.Sqlite.Repositories;
using SelfClaw.Infrastructure.Extensions;
using SelfClaw.Infrastructure.Extensions.Models;
using SelfClaw.Infrastructure.Extensions.Skills;
using SelfClaw.Infrastructure.Options;

namespace SelfClaw.Tests.Infrastructure.Extensions;

public sealed class ExtensionPackageInstallerTests : IDisposable
{
    private const string ValidManifest = """
        ---
        name: code/review
        description: Reviews code changes.
        version: 1.0.0
        triggers: [review, inspect]
        ---
        # Code review
        """;

    private const string ValidPluginManifest = """
        {
          "schemaVersion": 1,
          "id": "render-tools",
          "name": "Render Tools",
          "version": "1.0.0",
          "description": "Rendering tools",
          "permissions": ["process.execute"],
          "contributes": {
            "directInstructions": "instructions/direct.md",
            "skills": [],
            "mcpServers": [{
              "id": "renderer", "name": "Renderer", "transport": "stdio", "command": "node",
              "arguments": ["${pluginRoot}/server/index.js"], "requiresWorkspace": false, "requiredSettings": []
            }]
          }
        }
        """;

    private readonly string _rootPath = Path.Combine(
        Path.GetTempPath(),
        "SelfClawTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task InstallAsync_imports_nested_zip_atomically_and_defaults_to_disabled()
    {
        var context = await CreateContextAsync();
        var archivePath = Path.Combine(_rootPath, "review.selfclaw-skill");
        CreateArchive(archivePath,
        [
            ("package/SKILL.md", ValidManifest, 0),
            ("package/references/checklist.md", "Check tests.", 0)
        ]);

        var result = await context.Installer.InstallAsync(ExtensionKind.Skill, archivePath);
        var stored = await context.Repository.GetPackageAsync(ExtensionKind.Skill, "code/review");

        result.Package.Should().Be(stored);
        result.FileCount.Should().Be(2);
        stored!.IsEnabled.Should().BeFalse();
        stored.ContentHash.Should().MatchRegex("^sha256:[0-9a-f]{64}$");
        stored.ManifestJson.Should().Contain("\"triggers\":[\"review\",\"inspect\"]");
        stored.InstallPath.Should().Be(Path.Combine(_rootPath, "skills", "code", "review"));
        File.Exists(Path.Combine(stored.InstallPath, "SKILL.md")).Should().BeTrue();
        File.Exists(Path.Combine(stored.InstallPath, "references", "checklist.md")).Should().BeTrue();
        Directory.Exists(Path.Combine(_rootPath, "staging", "extensions")).Should().BeTrue();
        Directory.EnumerateFileSystemEntries(Path.Combine(_rootPath, "staging", "extensions")).Should().BeEmpty();
    }

    [Fact]
    public async Task InstallAsync_imports_selected_skill_file_with_its_resources()
    {
        var context = await CreateContextAsync();
        var sourceRoot = Path.Combine(_rootPath, "source-skill");
        Directory.CreateDirectory(Path.Combine(sourceRoot, "references"));
        await File.WriteAllTextAsync(Path.Combine(sourceRoot, "SKILL.md"), ValidManifest);
        await File.WriteAllTextAsync(Path.Combine(sourceRoot, "references", "guide.md"), "Guide");

        var result = await context.Installer.InstallAsync(
            ExtensionKind.Skill,
            Path.Combine(sourceRoot, "SKILL.md"));

        result.FileCount.Should().Be(2);
        File.Exists(Path.Combine(result.Package.InstallPath, "references", "guide.md")).Should().BeTrue();
    }

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("/absolute.txt")]
    [InlineData("resource.txt:payload")]
    [InlineData("folder/../escape.txt")]
    public async Task InstallAsync_rejects_unsafe_archive_paths(string unsafePath)
    {
        var context = await CreateContextAsync();
        var archivePath = Path.Combine(_rootPath, "unsafe.zip");
        CreateArchive(archivePath,
        [
            ("SKILL.md", ValidManifest, 0),
            (unsafePath, "unsafe", 0)
        ]);

        var action = () => context.Installer.InstallAsync(ExtensionKind.Skill, archivePath);

        await action.Should().ThrowAsync<InvalidDataException>();
        (await context.Repository.ListPackagesAsync()).Should().BeEmpty();
        File.Exists(Path.Combine(_rootPath, "escape.txt")).Should().BeFalse();
    }

    [Fact]
    public async Task InstallAsync_rejects_duplicate_case_insensitive_paths()
    {
        var context = await CreateContextAsync();
        var archivePath = Path.Combine(_rootPath, "duplicate.zip");
        CreateArchive(archivePath,
        [
            ("SKILL.md", ValidManifest, 0),
            ("References/Guide.md", "one", 0),
            ("references/guide.md", "two", 0)
        ]);

        var action = () => context.Installer.InstallAsync(ExtensionKind.Skill, archivePath);

        await action.Should().ThrowAsync<InvalidDataException>().WithMessage("*duplicate case-insensitive path*");
    }

    [Fact]
    public async Task InstallAsync_rejects_archive_symbolic_links()
    {
        var context = await CreateContextAsync();
        var archivePath = Path.Combine(_rootPath, "link.zip");
        CreateArchive(archivePath,
        [
            ("SKILL.md", ValidManifest, 0),
            ("linked", "target", 0xA000 << 16)
        ]);

        var action = () => context.Installer.InstallAsync(ExtensionKind.Skill, archivePath);

        await action.Should().ThrowAsync<InvalidDataException>().WithMessage("*symbolic link or reparse point*");
    }

    [Fact]
    public async Task InstallAsync_enforces_file_count_and_size_limits()
    {
        var limits = new ExtensionPackageLimits(64 * 1024, 64 * 1024, 2, 1024, 32 * 1024);
        var context = await CreateContextAsync(limits);
        var countArchive = Path.Combine(_rootPath, "too-many.zip");
        CreateArchive(countArchive,
        [
            ("SKILL.md", ValidManifest, 0),
            ("one.txt", "one", 0),
            ("two.txt", "two", 0)
        ]);
        var sizeArchive = Path.Combine(_rootPath, "too-large.zip");
        CreateArchive(sizeArchive,
        [
            ("SKILL.md", ValidManifest, 0),
            ("large.txt", new string('x', 1025), 0)
        ]);

        Func<Task> countAction = () => context.Installer.InstallAsync(ExtensionKind.Skill, countArchive);
        Func<Task> sizeAction = () => context.Installer.InstallAsync(ExtensionKind.Skill, sizeArchive);
        await countAction.Should().ThrowAsync<InvalidDataException>().WithMessage("*file limit*");
        await sizeAction.Should().ThrowAsync<InvalidDataException>().WithMessage("*byte limit*");
    }

    [Fact]
    public async Task Invalid_manifest_preserves_the_installed_version_and_database_record()
    {
        var context = await CreateContextAsync();
        var validArchive = Path.Combine(_rootPath, "valid.zip");
        CreateArchive(validArchive,
        [
            ("SKILL.md", ValidManifest, 0),
            ("marker.txt", "original", 0)
        ]);
        var first = await context.Installer.InstallAsync(ExtensionKind.Skill, validArchive);
        var invalidArchive = Path.Combine(_rootPath, "invalid.zip");
        CreateArchive(invalidArchive,
        [
            ("SKILL.md", "---\nname: code/review\n---\nReplacement", 0),
            ("marker.txt", "replacement", 0)
        ]);

        var action = () => context.Installer.InstallAsync(ExtensionKind.Skill, invalidArchive);

        await action.Should().ThrowAsync<InvalidDataException>();
        var stored = await context.Repository.GetPackageAsync(ExtensionKind.Skill, "code/review");
        stored.Should().Be(first.Package);
        (await File.ReadAllTextAsync(Path.Combine(first.Package.InstallPath, "marker.txt")))
            .Should().Be("original");
    }

    [Fact]
    public async Task ReconcileAsync_removes_orphan_staging_and_reports_missing_packages_as_broken()
    {
        var context = await CreateContextAsync();
        var orphanPath = Path.Combine(_rootPath, "staging", "extensions", "orphan");
        Directory.CreateDirectory(orphanPath);
        await File.WriteAllTextAsync(Path.Combine(orphanPath, "partial.txt"), "partial");
        var now = DateTimeOffset.UtcNow;
        await context.Repository.UpsertPackageAsync(new ExtensionPackageRecord(
            ExtensionKind.Skill,
            "missing",
            "missing",
            "1.0.0",
            "Missing",
            Path.Combine(_rootPath, "skills", "missing"),
            "sha256:missing",
            "{}",
            null,
            true,
            null,
            null,
            now,
            now));
        var catalog = new ExtensionCatalog(context.Repository, context.Repository, context.StoragePaths);

        await catalog.ReconcileAsync();

        Directory.Exists(orphanPath).Should().BeFalse();
        (await catalog.ListPackageViewsAsync(ExtensionKind.Skill))
            .Should().ContainSingle().Which.Status.Should().Be("broken");
    }

    [Fact]
    public async Task ReconcileAsync_removes_unreferenced_plugin_versions_and_keeps_current_version()
    {
        var context = await CreateContextAsync();
        var pluginRoot = Path.Combine(_rootPath, "plugins", "render-tools");
        var currentPath = Path.Combine(pluginRoot, "versions", "current-hash");
        var stalePath = Path.Combine(pluginRoot, "versions", "stale-hash");
        Directory.CreateDirectory(currentPath);
        Directory.CreateDirectory(stalePath);
        var now = DateTimeOffset.UtcNow;
        await context.Repository.UpsertPackageAsync(new ExtensionPackageRecord(
            ExtensionKind.Plugin,
            "render-tools",
            "Render Tools",
            "2.0.0",
            "Rendering tools",
            currentPath,
            "sha256:current-hash",
            ValidPluginManifest,
            null,
            false,
            null,
            null,
            now,
            now));
        var catalog = new ExtensionCatalog(context.Repository, context.Repository, context.StoragePaths);

        await catalog.ReconcileAsync();

        Directory.Exists(currentPath).Should().BeTrue();
        Directory.Exists(stalePath).Should().BeFalse();
    }

    [Fact]
    public async Task InstallAsync_imports_plugin_into_immutable_version_and_switches_current_pointer()
    {
        var context = await CreateContextAsync();
        var archivePath = Path.Combine(_rootPath, "plugin.selfclaw-plugin");
        CreateArchive(archivePath,
        [
            ("package/plugin.json", ValidPluginManifest, 0),
            ("package/instructions/direct.md", "Use the renderer.", 0),
            ("package/server/index.js", "process.stdin.resume()", 0)
        ]);

        var result = await context.Installer.InstallAsync(ExtensionKind.Plugin, archivePath);

        result.Package.IsEnabled.Should().BeFalse();
        result.Package.InstallPath.Should().Contain(Path.Combine("plugins", "render-tools", "versions"));
        File.Exists(Path.Combine(result.Package.InstallPath, "plugin.json")).Should().BeTrue();
        var currentPath = Path.Combine(_rootPath, "plugins", "render-tools", "current.json");
        File.Exists(currentPath).Should().BeTrue();
        (await File.ReadAllTextAsync(currentPath)).Should().Contain(result.Package.ContentHash);
    }

    [Fact]
    public async Task InstallAsync_plugin_upgrade_keeps_old_version_directory_and_enabled_state()
    {
        var context = await CreateContextAsync();
        var firstArchive = Path.Combine(_rootPath, "plugin-v1.zip");
        CreateArchive(firstArchive,
        [
            ("plugin.json", ValidPluginManifest, 0),
            ("instructions/direct.md", "v1", 0),
            ("server/index.js", "v1", 0)
        ]);
        var first = await context.Installer.InstallAsync(ExtensionKind.Plugin, firstArchive);
        await context.Repository.SetPackageEnabledAsync(ExtensionKind.Plugin, "render-tools", true);
        var secondArchive = Path.Combine(_rootPath, "plugin-v2.zip");
        CreateArchive(secondArchive,
        [
            ("plugin.json", ValidPluginManifest.Replace("1.0.0", "2.0.0", StringComparison.Ordinal), 0),
            ("instructions/direct.md", "v2", 0),
            ("server/index.js", "v2", 0)
        ]);

        var second = await context.Installer.InstallAsync(ExtensionKind.Plugin, secondArchive);

        second.Package.IsEnabled.Should().BeTrue();
        second.Package.InstallPath.Should().NotBe(first.Package.InstallPath);
        Directory.Exists(first.Package.InstallPath).Should().BeTrue();
        Directory.Exists(second.Package.InstallPath).Should().BeTrue();
    }

    public void Dispose()
    {
        if (!Directory.Exists(_rootPath))
        {
            return;
        }

        try
        {
            Directory.Delete(_rootPath, true);
        }
        catch (IOException)
        {
        }
    }

    private async Task<TestContext> CreateContextAsync(ExtensionPackageLimits? limits = null)
    {
        Directory.CreateDirectory(_rootPath);
        var storagePaths = new StoragePaths(
            _rootPath,
            Path.Combine(_rootPath, "selfclaw.db"),
            Path.Combine(_rootPath, "secrets"));
        var repository = new SqliteExtensionRepository(new SqliteDatabase(storagePaths));
        await repository.InitializeAsync();
        limits ??= new ExtensionPackageLimits(
            1024 * 1024,
            4 * 1024 * 1024,
            100,
            1024 * 1024,
            256 * 1024);
        var reader = new SkillPackageReader(limits);
        var installer = new ExtensionPackageInstaller(storagePaths, repository, reader, limits);
        return new TestContext(storagePaths, repository, installer);
    }

    private static void CreateArchive(
        string archivePath,
        IReadOnlyList<(string Path, string Content, int ExternalAttributes)> entries)
    {
        using var stream = new FileStream(archivePath, FileMode.CreateNew, FileAccess.ReadWrite);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        foreach (var item in entries)
        {
            var entry = archive.CreateEntry(item.Path, CompressionLevel.Fastest);
            if (item.ExternalAttributes != 0)
            {
                entry.ExternalAttributes = item.ExternalAttributes;
            }

            using var writer = new StreamWriter(entry.Open());
            writer.Write(item.Content);
        }
    }

    private sealed record TestContext(
        StoragePaths StoragePaths,
        SqliteExtensionRepository Repository,
        ExtensionPackageInstaller Installer);
}
