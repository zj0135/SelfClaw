using System.IO.Compression;
using FluentAssertions;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Infrastructure.Data.Sqlite;
using SelfClaw.Infrastructure.Data.Sqlite.Repositories;
using SelfClaw.Infrastructure.Extensions;
using SelfClaw.Infrastructure.Extensions.Abstractions;
using SelfClaw.Infrastructure.Extensions.Mcp;
using SelfClaw.Infrastructure.Extensions.Mcp.Models;
using SelfClaw.Infrastructure.Extensions.Models;
using SelfClaw.Infrastructure.Extensions.Plugins;
using SelfClaw.Infrastructure.Extensions.Skills;
using SelfClaw.Infrastructure.Options;

namespace SelfClaw.Tests.Infrastructure.Extensions;

public sealed class ExtensionSettingsServiceTests : IDisposable
{
    private readonly string _rootPath;

    public ExtensionSettingsServiceTests()
    {
        _rootPath = Path.Combine(Path.GetTempPath(), "SelfClawTests", Guid.NewGuid().ToString("N"));
    }

    [Fact]
    public async Task SaveMcpServer_applies_secret_three_state_rule_and_returns_only_masked_state()
    {
        var context = CreateContext();
        var create = CreateStdioCommand(
            environment:
            [
                new McpKeyValueCommand("LOG_LEVEL", "debug", false),
                new McpKeyValueCommand("API_TOKEN", "plain-secret", true)
            ]);

        var createdView = await context.Service.SaveMcpServerAsync(create);
        var created = await context.Repository.GetMcpServerAsync(createdView.Id);

        created.Should().NotBeNull();
        created!.SettingsJson.Should().Contain("API_TOKEN");
        created.SettingsJson.Should().Contain("debug");
        created.SettingsJson.Should().NotContain("plain-secret");
        created.SettingsJson.Should().NotContain("secret:1");
        created.CredentialRefs.Should().Contain("environment.API_TOKEN", "secret:1");
        createdView.Environment.Single(entry => entry.Key == "API_TOKEN").Should().Be(
            new McpConfigurationEntryView("API_TOKEN", null, true, true));

        await context.Service.SaveMcpServerAsync(create with
        {
            Id = created.Id,
            Environment =
            [
                new McpKeyValueCommand("LOG_LEVEL", "info", false),
                new McpKeyValueCommand("API_TOKEN", null, true)
            ]
        });
        context.SecretProtector.StoreCalls.Should().ContainSingle();

        await context.Service.SaveMcpServerAsync(create with
        {
            Id = created.Id,
            Environment = [new McpKeyValueCommand("API_TOKEN", "replacement", true)]
        });
        context.SecretProtector.StoreCalls.Should().HaveCount(2);
        context.SecretProtector.StoreCalls[1].ExistingSecretRef.Should().Be("secret:1");

        var clearedView = await context.Service.SaveMcpServerAsync(create with
        {
            Id = created.Id,
            Environment = [new McpKeyValueCommand("API_TOKEN", null, true, ClearSecret: true)]
        });
        var cleared = await context.Repository.GetMcpServerAsync(created.Id);
        cleared!.CredentialRefs.Should().BeEmpty();
        clearedView.Environment.Should().ContainSingle().Which.HasSecret.Should().BeFalse();
        context.SecretProtector.DeleteCalls.Should().Contain("secret:1");
    }

    [Fact]
    public async Task SaveMcpServer_rejects_invalid_transport_configuration_without_mutating_state()
    {
        var context = CreateContext();
        var remoteHttp = new SaveMcpServerCommand(
            null,
            "Remote insecure",
            McpTransportKind.Http,
            null,
            [],
            null,
            false,
            [],
            "http://mcp.example.test/api",
            "auto",
            15,
            []);

        var remoteAction = () => context.Service.SaveMcpServerAsync(remoteHttp);
        var embeddedCredentialAction = () => context.Service.SaveMcpServerAsync(remoteHttp with
        {
            Endpoint = "https://token@mcp.example.test/api"
        });
        var environmentAction = () => context.Service.SaveMcpServerAsync(
            CreateStdioCommand(environment: [new McpKeyValueCommand("INVALID-KEY", "x", false)]));
        var argumentsAction = () => context.Service.SaveMcpServerAsync(
            CreateStdioCommand() with { Arguments = null! });

        await remoteAction.Should().ThrowAsync<ArgumentException>().WithMessage("*HTTPS*");
        await embeddedCredentialAction.Should().ThrowAsync<ArgumentException>().WithMessage("*credentials*");
        await environmentAction.Should().ThrowAsync<ArgumentException>().WithMessage("*Environment key*");
        await argumentsAction.Should().ThrowAsync<ArgumentNullException>();
        (await context.Repository.ListMcpServersAsync()).Should().BeEmpty();
        (await context.Service.GetStateAsync()).Revision.Should().Be(0);

        var loopback = remoteHttp with
        {
            DisplayName = "Local HTTP",
            Endpoint = "http://localhost:4312/mcp"
        };
        (await context.Service.SaveMcpServerAsync(loopback)).Status.Should().Be("disabled");
    }

    [Fact]
    public async Task GetState_aggregates_catalog_status_and_increments_revision_for_mutations()
    {
        var context = CreateContext();
        var now = DateTimeOffset.UtcNow;
        var installedPath = Path.Combine(_rootPath, "skills", "review");
        Directory.CreateDirectory(installedPath);
        await File.WriteAllTextAsync(
            Path.Combine(installedPath, "SKILL.md"),
            "---\nname: Review\ndescription: Reviews changes\n---\nReview instructions.");
        await context.Repository.UpsertPackageAsync(new ExtensionPackageRecord(
            ExtensionKind.Skill,
            "review",
            "Review",
            "1.0.0",
            "Reviews changes",
            installedPath,
            "sha256:review",
            "{}",
            null,
            true,
            null,
            null,
            now,
            now));
        await context.Repository.UpsertPackageAsync(new ExtensionPackageRecord(
            ExtensionKind.Plugin,
            "missing-plugin",
            "Missing plugin",
            "1.0.0",
            "Missing on disk",
            Path.Combine(_rootPath, "plugins", "missing"),
            "sha256:missing",
            "{}",
            null,
            true,
            null,
            null,
            now,
            now));

        var initial = await context.Service.GetStateAsync();
        initial.Revision.Should().Be(0);
        initial.Skills.Should().ContainSingle().Which.Status.Should().Be("ready");
        initial.Plugins.Should().ContainSingle().Which.Status.Should().Be("broken");

        await context.Service.SetEnabledAsync(
            new ExtensionItemKey(ExtensionKind.Skill, "review"),
            false);
        var updated = await context.Service.GetStateAsync();
        updated.Revision.Should().Be(1);
        updated.Skills.Should().ContainSingle().Which.Status.Should().Be("disabled");

        await context.Service.DeleteAsync(new ExtensionItemKey(ExtensionKind.Skill, "review"));
        (await context.Service.GetStateAsync()).Revision.Should().Be(2);
    }

    [Fact]
    public async Task GetState_reports_a_skill_without_its_entry_file_as_broken()
    {
        var context = CreateContext();
        var now = DateTimeOffset.UtcNow;
        var installedPath = Path.Combine(_rootPath, "skills", "review");
        Directory.CreateDirectory(installedPath);
        await context.Repository.UpsertPackageAsync(new ExtensionPackageRecord(
            ExtensionKind.Skill,
            "review",
            "Review",
            "1.0.0",
            "Reviews changes",
            installedPath,
            "sha256:review",
            "{}",
            null,
            true,
            null,
            null,
            now,
            now));

        var state = await context.Service.GetStateAsync();

        state.Skills.Should().ContainSingle().Which.Status.Should().Be("broken");
    }

    [Fact]
    public async Task Plugin_enable_requires_current_manifest_permissions_to_be_acknowledged()
    {
        var context = CreateContext();
        var pluginPath = Path.Combine(_rootPath, "plugins", "office", "versions", "v1");
        Directory.CreateDirectory(pluginPath);
        var initialManifest = """
            {"schemaVersion":1,"id":"office","name":"Office","version":"1.0.0",
             "permissions":["workspace.read"],"contributes":{"skills":[],"mcpServers":[]}}
            """;
        await File.WriteAllTextAsync(Path.Combine(pluginPath, "plugin.json"), initialManifest);
        var now = DateTimeOffset.UtcNow;
        var plugin = new ExtensionPackageRecord(
            ExtensionKind.Plugin,
            "office",
            "Office",
            "1.0.0",
            "Office workflows",
            pluginPath,
            "sha256:v1",
            initialManifest,
            null,
            false,
            null,
            null,
            now,
            now);
        await context.Repository.UpsertPackageAsync(plugin);

        var unconfirmed = () => context.Service.SetEnabledAsync(
            new ExtensionItemKey(ExtensionKind.Plugin, "office"), true);
        var spoofed = () => context.Service.AcknowledgePluginPermissionsAsync(
            "office", ["workspace.read", "process.execute"]);

        await unconfirmed.Should().ThrowAsync<InvalidOperationException>().WithMessage("*permission confirmation*");
        await spoofed.Should().ThrowAsync<InvalidOperationException>().WithMessage("*changed before confirmation*");
        await context.Service.AcknowledgePluginPermissionsAsync("office", ["workspace.read"]);
        await context.Service.SetEnabledAsync(new ExtensionItemKey(ExtensionKind.Plugin, "office"), true);
        (await context.Repository.GetPackageAsync(ExtensionKind.Plugin, "office"))!.IsEnabled.Should().BeTrue();

        var upgradedManifest = """
            {"schemaVersion":1,"id":"office","name":"Office","version":"2.0.0",
             "permissions":["process.execute","workspace.read"],"contributes":{"skills":[],"mcpServers":[]}}
            """;
        await File.WriteAllTextAsync(Path.Combine(pluginPath, "plugin.json"), upgradedManifest);
        var upgraded = (await context.Repository.GetPackageAsync(ExtensionKind.Plugin, "office"))! with
        {
            ManifestJson = upgradedManifest,
            Version = "2.0.0",
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };
        await context.Repository.UpsertPackageAsync(upgraded);

        var state = await context.Service.GetStateAsync();
        state.Plugins.Single().Status.Should().Be("needs-permission");
        state.Plugins.Single().UnacknowledgedPermissions.Should().Equal("process.execute");
        await unconfirmed.Should().ThrowAsync<InvalidOperationException>().WithMessage("*process.execute*");
    }

    [Fact]
    public async Task ImportPlugin_materializes_managed_mcp_and_required_settings_block_startup()
    {
        var context = CreateContext();
        Directory.CreateDirectory(_rootPath);
        var archivePath = Path.Combine(_rootPath, "render-tools.selfclaw-plugin");
        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            var manifest = archive.CreateEntry("plugin.json");
            await using (var stream = manifest.Open())
            await using (var writer = new StreamWriter(stream))
            {
                await writer.WriteAsync("""
                    {
                      "schemaVersion": 1,
                      "id": "render-tools",
                      "name": "Render Tools",
                      "version": "1.0.0",
                      "permissions": ["process.execute"],
                      "contributes": {
                        "skills": [],
                        "mcpServers": [{
                          "id": "renderer",
                          "name": "Renderer",
                          "transport": "stdio",
                          "command": "node",
                          "arguments": ["${pluginRoot}/server.js"],
                          "requiresWorkspace": false,
                          "requiredSettings": [{"key":"LICENSE_KEY","target":"env","secret":true}]
                        }]
                      }
                    }
                    """);
            }

            var server = archive.CreateEntry("server.js");
            await using var serverStream = server.Open();
            await using var serverWriter = new StreamWriter(serverStream);
            await serverWriter.WriteAsync("process.stdin.resume();");
        }

        var view = await context.Service.ImportPackageAsync(ExtensionKind.Plugin, archivePath);
        var managed = await context.Repository.GetMcpServerAsync("render-tools/renderer");

        view.Permissions.Should().Equal("process.execute");
        managed.Should().NotBeNull();
        managed!.SourcePluginId.Should().Be("render-tools");
        managed.IsEnabled.Should().BeFalse();
        var settings = ExtensionCatalog.DeserializeSettings(managed.SettingsJson);
        settings.RequiredFieldNames.Should().Equal("environment.LICENSE_KEY");

        await context.Service.AcknowledgePluginPermissionsAsync("render-tools", ["process.execute"]);
        await context.Service.SetEnabledAsync(new ExtensionItemKey(ExtensionKind.Plugin, "render-tools"), true);
        (await context.Repository.GetMcpServerAsync(managed.Id))!.IsEnabled.Should().BeTrue();

        var tamperedSave = () => context.Service.SaveMcpServerAsync(new SaveMcpServerCommand(
            managed.Id,
            "Tampered renderer",
            McpTransportKind.Http,
            null,
            [],
            null,
            false,
            [],
            "https://example.com/mcp",
            "auto",
            30,
            [],
            Enabled: false));
        await tamperedSave.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*structure cannot be changed*");

        var saved = await context.Service.SaveMcpServerAsync(new SaveMcpServerCommand(
            managed.Id,
            managed.DisplayName,
            managed.Transport,
            settings.Command,
            settings.Arguments,
            settings.WorkingDirectoryMode,
            settings.RequiresWorkspace,
            [new McpKeyValueCommand("LICENSE_KEY", "licensed", IsSecret: true, ClearSecret: false)],
            settings.Endpoint,
            settings.TransportMode,
            settings.ConnectionTimeoutSeconds,
            [],
            Enabled: true));

        saved.Name.Should().Be("Renderer");
        saved.Transport.Should().Be("stdio");
        saved.Enabled.Should().BeTrue();
        saved.Environment.Should().ContainSingle(entry =>
            entry.Key == "LICENSE_KEY" && entry.IsSecret && entry.HasSecret && entry.Value == null);
    }

    [Fact]
    public async Task DeletePlugin_waits_for_active_version_lease_before_removing_files()
    {
        var context = CreateContext();
        var pluginPath = Path.Combine(_rootPath, "plugins", "office", "versions", "v1");
        Directory.CreateDirectory(pluginPath);
        var manifest = """
            {"schemaVersion":1,"id":"office","name":"Office","version":"1.0.0",
             "permissions":[],"contributes":{"skills":[],"mcpServers":[]}}
            """;
        await File.WriteAllTextAsync(Path.Combine(pluginPath, "plugin.json"), manifest);
        var now = DateTimeOffset.UtcNow;
        await context.Repository.UpsertPackageAsync(new ExtensionPackageRecord(
            ExtensionKind.Plugin, "office", "Office", "1.0.0", "", pluginPath,
            "sha256:v1", manifest, null, true, "[]", now, now, now));
        var lease = context.PluginVersionLeaseManager.Acquire(pluginPath);

        var deleteTask = context.Service.DeleteAsync(new ExtensionItemKey(ExtensionKind.Plugin, "office"));

        deleteTask.IsCompleted.Should().BeFalse();
        Directory.Exists(pluginPath).Should().BeTrue();
        await lease.DisposeAsync();
        await deleteTask;
        Directory.Exists(Path.Combine(_rootPath, "plugins", "office")).Should().BeFalse();
        (await context.Repository.GetPackageAsync(ExtensionKind.Plugin, "office")).Should().BeNull();
    }

    [Fact]
    public async Task DeletePlugin_waits_for_a_lease_on_an_older_installed_version()
    {
        var context = CreateContext();
        var pluginRoot = Path.Combine(_rootPath, "plugins", "office");
        var oldPath = Path.Combine(pluginRoot, "versions", "v1");
        var currentPath = Path.Combine(pluginRoot, "versions", "v2");
        Directory.CreateDirectory(oldPath);
        Directory.CreateDirectory(currentPath);
        var manifest = """
            {"schemaVersion":1,"id":"office","name":"Office","version":"2.0.0",
             "permissions":[],"contributes":{"skills":[],"mcpServers":[]}}
            """;
        await File.WriteAllTextAsync(Path.Combine(currentPath, "plugin.json"), manifest);
        var now = DateTimeOffset.UtcNow;
        await context.Repository.UpsertPackageAsync(new ExtensionPackageRecord(
            ExtensionKind.Plugin, "office", "Office", "2.0.0", "", currentPath,
            "sha256:v2", manifest, null, true, "[]", now, now, now));
        var oldVersionLease = context.PluginVersionLeaseManager.Acquire(oldPath);

        var deleteTask = context.Service.DeleteAsync(new ExtensionItemKey(ExtensionKind.Plugin, "office"));

        deleteTask.IsCompleted.Should().BeFalse();
        Directory.Exists(oldPath).Should().BeTrue();
        await oldVersionLease.DisposeAsync();
        await deleteTask;
        Directory.Exists(pluginRoot).Should().BeFalse();
    }

    [Fact]
    public async Task DeleteMcpServer_deletes_all_protected_secrets_and_the_database_row()
    {
        var context = CreateContext();
        var view = await context.Service.SaveMcpServerAsync(CreateStdioCommand(
            environment:
            [
                new McpKeyValueCommand("FIRST", "one", true),
                new McpKeyValueCommand("SECOND", "two", true)
            ]));

        await context.Service.DeleteAsync(new ExtensionItemKey(ExtensionKind.McpServer, view.Id));

        context.SecretProtector.DeleteCalls.Should().BeEquivalentTo("secret:1", "secret:2");
        (await context.Repository.GetMcpServerAsync(view.Id)).Should().BeNull();
        (await context.Service.GetStateAsync()).Revision.Should().Be(2);
    }

    [Fact]
    public async Task TestMcpServerAsync_persists_health_and_discovered_tools()
    {
        var context = CreateContext();
        var view = await context.Service.SaveMcpServerAsync(CreateStdioCommand() with
        {
            WorkingDirectoryMode = "appData",
            RequiresWorkspace = false
        });
        context.McpClientManager.Result = new McpHealthResult(
            view.Id,
            McpServerHealthStatus.Ready,
            12,
            null,
            ["one", "two"]);

        var result = await context.Service.TestMcpServerAsync(view.Id);

        result.Status.Should().Be(McpServerHealthStatus.Ready);
        var stored = await context.Repository.GetMcpServerAsync(view.Id);
        stored!.DiscoveredTools.Should().Equal("one", "two");
        stored.LastStatus.Should().Be(McpServerHealthStatus.Ready);
        stored.LastCheckedAtUtc.Should().NotBeNull();
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

    private TestContext CreateContext()
    {
        var storagePaths = new StoragePaths(
            _rootPath,
            Path.Combine(_rootPath, "selfclaw.db"),
            Path.Combine(_rootPath, "secrets"));
        var repository = new SqliteExtensionRepository(new SqliteDatabase(storagePaths));
        var protector = new FakeSecretProtector();
        var limits = new ExtensionPackageLimits(
            100L * 1024 * 1024,
            300L * 1024 * 1024,
            5000,
            50L * 1024 * 1024,
            256L * 1024);
        var reader = new SkillPackageReader(limits);
        var pluginReader = new PluginManifestReader(limits);
        var catalog = new ExtensionCatalog(repository, repository, storagePaths, pluginReader);
        var installer = new ExtensionPackageInstaller(storagePaths, repository, reader, pluginReader, limits);
        var mcpClientManager = new FakeMcpClientManager();
        var pluginVersionLeaseManager = new PluginVersionLeaseManager();
        var pluginContributionService = new PluginContributionService(
            repository,
            protector,
            mcpClientManager,
            pluginReader);
        var stateChangeNotifier = new ExtensionStateChangeNotifier();
        return new TestContext(
            new ExtensionSettingsService(
                repository,
                repository,
                protector,
                catalog,
                installer,
                new McpConfigurationResolver(protector, storagePaths),
                mcpClientManager,
                pluginContributionService,
                stateChangeNotifier,
                pluginVersionLeaseManager),
            repository,
            protector,
            mcpClientManager,
            pluginVersionLeaseManager);
    }

    private static SaveMcpServerCommand CreateStdioCommand(
        IReadOnlyList<McpKeyValueCommand>? environment = null)
        => new(
            null,
            "Local tools",
            McpTransportKind.Stdio,
            "node",
            ["server.js", "argument with spaces"],
            "workspace",
            true,
            environment ?? [],
            null,
            null,
            null,
            [],
            Enabled: true);

    private sealed record TestContext(
        ExtensionSettingsService Service,
        SqliteExtensionRepository Repository,
        FakeSecretProtector SecretProtector,
        FakeMcpClientManager McpClientManager,
        PluginVersionLeaseManager PluginVersionLeaseManager);

    private sealed class FakeMcpClientManager : IMcpClientManager
    {
        public McpHealthResult Result { get; set; } = new(
            "server",
            McpServerHealthStatus.Degraded,
            null,
            "not configured",
            []);

        public Task<McpClientLease> AcquireAsync(
            ResolvedMcpServerConfiguration configuration,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<McpHealthResult> TestAsync(
            ResolvedMcpServerConfiguration configuration,
            CancellationToken cancellationToken = default)
            => Task.FromResult(Result with { Id = configuration.Id });

        public Task DrainAsync(string serverId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class FakeSecretProtector : ISecretProtector
    {
        private int _nextId;

        public List<(string Secret, string? ExistingSecretRef)> StoreCalls { get; } = [];
        public List<string> DeleteCalls { get; } = [];
        public Dictionary<string, string> Secrets { get; } = [];

        public Task<string> StoreSecretAsync(
            string secret,
            string? existingSecretRef = null,
            CancellationToken cancellationToken = default)
        {
            StoreCalls.Add((secret, existingSecretRef));
            var secretRef = existingSecretRef ?? $"secret:{++_nextId}";
            Secrets[secretRef] = secret;
            return Task.FromResult(secretRef);
        }

        public Task<string?> RetrieveSecretAsync(
            string secretRef,
            CancellationToken cancellationToken = default)
            => Task.FromResult(Secrets.GetValueOrDefault(secretRef));

        public Task DeleteSecretAsync(
            string secretRef,
            CancellationToken cancellationToken = default)
        {
            DeleteCalls.Add(secretRef);
            Secrets.Remove(secretRef);
            return Task.CompletedTask;
        }
    }
}
