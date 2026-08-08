using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Infrastructure.Data.Sqlite;
using SelfClaw.Infrastructure.Data.Sqlite.Repositories;
using SelfClaw.Infrastructure.Extensions.Discovery;
using SelfClaw.Infrastructure.Extensions.Models;
using SelfClaw.Infrastructure.Extensions.Skills;
using SelfClaw.Infrastructure.Options;

namespace SelfClaw.Tests.Infrastructure.Extensions;

public sealed class UserSkillDiscoveryServiceTests : IDisposable
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

    private readonly string _rootPath = Path.Combine(
        Path.GetTempPath(),
        "SelfClawTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task DiscoverAsync_registers_two_skills_from_user_skills_root()
    {
        var context = await CreateContextAsync();
        WriteSkill("code/review", ValidManifest);
        WriteSkill("summarize", """
            ---
            name: summarize
            description: Summarizes text.
            ---
            # Summarize
            """);

        await context.Service.DiscoverAndRegisterAsync();

        var packages = await context.Repository.ListPackagesAsync();
        var review = packages.Single(package => package.Id == "code/review");
        var summarize = packages.Single(package => package.Id == "summarize");
        review.Kind.Should().Be(ExtensionKind.Skill);
        review.IsEnabled.Should().BeFalse();
        review.SourcePluginId.Should().BeNull();
        review.InstallPath.Should().Be(Path.Combine(context.Root, "code", "review"));
        review.ContentHash.Should().MatchRegex("^sha256:[0-9a-f]{64}$");
        review.ManifestJson.Should().Contain("\"schemaVersion\":1");
        review.ManifestJson.Should().Contain("\"triggers\"");
        summarize.InstallPath.Should().Be(Path.Combine(context.Root, "summarize"));
    }

    [Fact]
    public async Task DiscoverAsync_skips_broken_skill_and_registers_valid_ones()
    {
        var context = await CreateContextAsync();
        WriteSkill("valid", ValidManifest.Replace("name: code/review", "name: valid", StringComparison.Ordinal));
        WriteSkill("broken", "---\nname: broken\n---\nNo description");

        await context.Service.DiscoverAndRegisterAsync();

        var packages = await context.Repository.ListPackagesAsync();
        packages.Should().ContainSingle().Which.Id.Should().Be("valid");
    }

    [Fact]
    public async Task DiscoverAsync_missing_root_is_noop()
    {
        var context = await CreateContextAsync();

        var act = () => context.Service.DiscoverAndRegisterAsync();

        await act.Should().NotThrowAsync();
        (await context.Repository.ListPackagesAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task DiscoverAsync_rescan_preserves_enabled_state_when_content_unchanged()
    {
        var context = await CreateContextAsync();
        WriteSkill("code/review", ValidManifest);
        await context.Service.DiscoverAndRegisterAsync();
        await context.Repository.SetPackageEnabledAsync(ExtensionKind.Skill, "code/review", enabled: true);
        var afterEnable = await context.Repository.GetPackageAsync(ExtensionKind.Skill, "code/review");

        await context.Service.DiscoverAndRegisterAsync();

        var after = await context.Repository.GetPackageAsync(ExtensionKind.Skill, "code/review");
        after!.IsEnabled.Should().BeTrue();
        after.UpdatedAtUtc.Should().Be(afterEnable!.UpdatedAtUtc);
    }

    [Fact]
    public async Task DiscoverAsync_rescan_updates_record_when_content_changed_but_keeps_enabled()
    {
        var context = await CreateContextAsync();
        WriteSkill("code/review", ValidManifest);
        await context.Service.DiscoverAndRegisterAsync();
        await context.Repository.SetPackageEnabledAsync(ExtensionKind.Skill, "code/review", enabled: true);
        var before = await context.Repository.GetPackageAsync(ExtensionKind.Skill, "code/review");

        WriteSkill("code/review", ValidManifest.Replace("version: 1.0.0", "version: 2.0.0", StringComparison.Ordinal));
        await context.Service.DiscoverAndRegisterAsync();

        var after = await context.Repository.GetPackageAsync(ExtensionKind.Skill, "code/review");
        after!.Version.Should().Be("2.0.0");
        after.IsEnabled.Should().BeTrue();
        after.UpdatedAtUtc.Should().BeAfter(before!.UpdatedAtUtc);
    }

    [Fact]
    public async Task DiscoverAsync_does_not_clobber_installed_skill_with_same_id()
    {
        var context = await CreateContextAsync();
        WriteSkill("code/review", ValidManifest);
        var installedPath = Path.Combine(_rootPath, "installed", "skills", "code", "review");
        var now = DateTimeOffset.UtcNow;
        await context.Repository.UpsertPackageAsync(new ExtensionPackageRecord(
            ExtensionKind.Skill,
            "code/review",
            "Code review",
            "1.0.0",
            "Reviews code changes.",
            installedPath,
            "sha256:installed",
            "{}",
            SourcePluginId: null,
            IsEnabled: true,
            AcknowledgedPermissionsJson: null,
            AcknowledgedAtUtc: null,
            InstalledAtUtc: now,
            UpdatedAtUtc: now));

        await context.Service.DiscoverAndRegisterAsync();

        var stored = await context.Repository.GetPackageAsync(ExtensionKind.Skill, "code/review");
        stored!.InstallPath.Should().Be(installedPath);
        stored.ContentHash.Should().Be("sha256:installed");
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

    private void WriteSkill(string relativeId, string manifest)
    {
        var skillDirectory = Path.Combine(_rootPath, "agents", "skills", relativeId.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(skillDirectory);
        File.WriteAllText(Path.Combine(skillDirectory, "SKILL.md"), manifest);
    }

    private async Task<TestContext> CreateContextAsync()
    {
        Directory.CreateDirectory(_rootPath);
        var storagePaths = new StoragePaths(
            _rootPath,
            Path.Combine(_rootPath, "selfclaw.db"),
            Path.Combine(_rootPath, "secrets"));
        var repository = new SqliteExtensionRepository(new SqliteDatabase(storagePaths));
        await repository.InitializeAsync();
        var limits = new ExtensionPackageLimits(
            1024 * 1024,
            4 * 1024 * 1024,
            100,
            1024 * 1024,
            256 * 1024);
        var reader = new SkillPackageReader(limits);
        var root = Path.Combine(_rootPath, "agents", "skills");
        var service = new UserSkillDiscoveryService(
            root,
            repository,
            reader,
            NullLogger<UserSkillDiscoveryService>.Instance);
        return new TestContext(root, repository, service);
    }

    private sealed record TestContext(
        string Root,
        SqliteExtensionRepository Repository,
        UserSkillDiscoveryService Service);
}
