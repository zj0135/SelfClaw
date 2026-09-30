using System.IO.Compression;
using FluentAssertions;
using SelfClaw.Core.Models;
using SelfClaw.Infrastructure.Data.Sqlite;
using SelfClaw.Infrastructure.Data.Sqlite.Repositories;
using SelfClaw.Infrastructure.Extensions;
using SelfClaw.Infrastructure.Extensions.Models;
using SelfClaw.Infrastructure.Extensions.Plugins;
using SelfClaw.Infrastructure.Extensions.Skills;
using SelfClaw.Infrastructure.Options;

namespace SelfClaw.Tests.Infrastructure.Extensions;

/// <summary>
/// Installs the checked-in demo package through the real installer. It is the only test that proves the
/// documented end-to-end fixture still imports, so a manifest change that breaks it fails here rather
/// than during a manual walkthrough.
/// </summary>
public sealed class ViewPluginFixtureTests : IDisposable
{
    private readonly string _rootPath = Path.Combine(
        Path.GetTempPath(), "SelfClawTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task The_demo_package_installs_and_exposes_both_view_slots()
    {
        var storagePaths = StoragePathDefaults.Create(
            _rootPath,
            Path.Combine(_rootPath, "selfclaw.db"),
            Path.Combine(_rootPath, "secrets"));
        var repository = new SqliteExtensionRepository(new SqliteDatabase(storagePaths));
        await repository.InitializeAsync();
        var limits = new ExtensionPackageLimits(
            100L * 1024 * 1024, 300L * 1024 * 1024, 5000, 50L * 1024 * 1024, 256L * 1024);
        var pluginReader = new PluginManifestReader(limits);
        var installer = new ExtensionPackageInstaller(
            storagePaths,
            repository,
            new SkillPackageReader(limits),
            pluginReader,
            limits);
        var catalog = new ExtensionCatalog(repository, repository, storagePaths, pluginReader);

        var installed = await installer.InstallAsync(ExtensionKind.Plugin, CreateFixturePackage());

        installed.Package.Id.Should().Be("view-demo");
        // Imported packages start disabled: enabling is the moment the user grants the capabilities.
        installed.Package.IsEnabled.Should().BeFalse();

        var views = (await catalog.ListPluginViewsAsync()).Should().HaveCount(2).And.Subject;

        var docked = views.Single(view => view.Slot == PluginViewSlot.Right);
        docked.Key.Should().Be("view-demo/inspector");
        docked.Url.Should().Be("https://view-demo.plugin.selfclaw.local/ui/inspector.html");
        docked.DefaultWidth.Should().Be(400);
        docked.Status.Should().Be(ExtensionStatus.Disabled);
        docked.NetworkOrigins.Should().BeEmpty();
        docked.Permissions.Should().Contain("host.workspace.read");

        var floating = views.Single(view => view.Slot == PluginViewSlot.Floating);
        floating.Key.Should().Be("view-demo/hud");
        floating.Url.Should().Be("https://view-demo.plugin.selfclaw.local/ui/hud.html");
        floating.DefaultWidth.Should().BeNull();
        floating.Permissions.Should().Contain("ui.floating");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_rootPath)) Directory.Delete(_rootPath, true);
        }
        catch (IOException)
        {
        }
    }

    // Zipped from the checked-in source directory at test time rather than committing a binary, so the
    // package can never drift from the files a reader is looking at.
    private string CreateFixturePackage()
    {
        var sourcePath = Path.Combine(
            AppContext.BaseDirectory,
            "Infrastructure",
            "Extensions",
            "Fixtures",
            "view-plugin");
        Directory.Exists(sourcePath).Should().BeTrue($"the demo package sources belong at {sourcePath}");
        Directory.CreateDirectory(_rootPath);
        var archivePath = Path.Combine(_rootPath, "view-demo.zip");
        ZipFile.CreateFromDirectory(sourcePath, archivePath);
        return archivePath;
    }
}
