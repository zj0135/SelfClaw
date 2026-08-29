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
public sealed class PanelPluginFixtureTests : IDisposable
{
    private readonly string _rootPath = Path.Combine(
        Path.GetTempPath(), "SelfClawTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task The_demo_panel_package_installs_and_exposes_its_panel()
    {
        var storagePaths = new StoragePaths(
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

        installed.Package.Id.Should().Be("panel-demo");
        // Imported packages start disabled: enabling is the moment the user grants the capabilities.
        installed.Package.IsEnabled.Should().BeFalse();

        var panel = (await catalog.ListPluginPanelViewsAsync()).Should().ContainSingle().Subject;
        panel.Key.Should().Be("panel-demo/inspector");
        panel.Url.Should().Be("https://panel-demo.plugin.selfclaw.local/ui/inspector.html");
        panel.Status.Should().Be(ExtensionStatus.Disabled);
        panel.NetworkOrigins.Should().BeEmpty();
        panel.Permissions.Should().Contain("host.workspace.read");
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
            "panel-plugin");
        Directory.Exists(sourcePath).Should().BeTrue($"the demo package sources belong at {sourcePath}");
        Directory.CreateDirectory(_rootPath);
        var archivePath = Path.Combine(_rootPath, "panel-demo.zip");
        ZipFile.CreateFromDirectory(sourcePath, archivePath);
        return archivePath;
    }
}
