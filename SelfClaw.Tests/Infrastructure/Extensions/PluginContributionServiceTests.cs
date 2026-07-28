using System.Text.Json;
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
using SelfClaw.Infrastructure.Options;

namespace SelfClaw.Tests.Infrastructure.Extensions;

/// <summary>
/// Covers the manifest -> MCP record projection: namespaced ids, required/secret path derivation, which
/// values survive a re-sync, and the drain-before-delete ordering that keeps an active turn intact.
/// </summary>
public sealed class PluginContributionServiceTests : IDisposable
{
    private readonly string _rootPath = Path.Combine(
        Path.GetTempPath(),
        "SelfClawTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task SynchronizeMcpServersAsync_namespaces_ids_and_derives_required_and_secret_paths()
    {
        var context = CreateContext();
        var plugin = await CreatePluginAsync(context, """
            {
              "schemaVersion": 1,
              "id": "office",
              "name": "Office",
              "version": "1.0.0",
              "contributes": {
                "mcpServers": [
                  {
                    "id": "renderer",
                    "name": "Office Renderer",
                    "transport": "stdio",
                    "command": "node",
                    "arguments": ["${pluginRoot}/server/index.js"],
                    "requiresWorkspace": true,
                    "requiredSettings": [
                      { "key": "LICENSE_KEY", "target": "env", "secret": true },
                      { "key": "LOG_LEVEL", "target": "env", "secret": false }
                    ]
                  }
                ]
              }
            }
            """);

        await context.Service.SynchronizeMcpServersAsync(plugin, CancellationToken.None);

        var server = await context.Repository.GetMcpServerAsync("office/renderer");
        server.Should().NotBeNull();
        server!.SourcePluginId.Should().Be("office");
        server.Transport.Should().Be(McpTransportKind.Stdio);
        var settings = ExtensionCatalog.DeserializeSettings(server.SettingsJson);
        settings.Command.Should().Be("node");
        settings.Arguments.Should().Equal("${pluginRoot}/server/index.js");
        settings.WorkingDirectoryMode.Should().Be("plugin");
        settings.RequiresWorkspace.Should().BeTrue();
        settings.SecretFieldNames.Should().Equal("environment.LICENSE_KEY");
        settings.RequiredFieldNames.Should().Equal("environment.LICENSE_KEY", "environment.LOG_LEVEL");
        // Only the non-secret required setting gets a placeholder row; the secret one lives in credentialRefs.
        settings.Environment.Should().ContainKey("LOG_LEVEL").WhoseValue.Should().BeEmpty();
        settings.Environment.Should().NotContainKey("LICENSE_KEY");
    }

    [Fact]
    public async Task SynchronizeMcpServersAsync_stays_disabled_until_permissions_are_acknowledged()
    {
        var context = CreateContext();
        var manifest = """
            {
              "schemaVersion": 1,
              "id": "office",
              "name": "Office",
              "version": "1.0.0",
              "permissions": ["process.execute"],
              "contributes": {
                "mcpServers": [
                  {
                    "id": "renderer",
                    "name": "Office Renderer",
                    "transport": "stdio",
                    "command": "node",
                    "arguments": []
                  }
                ]
              }
            }
            """;
        var plugin = await CreatePluginAsync(context, manifest, isEnabled: true);

        await context.Service.SynchronizeMcpServersAsync(plugin, CancellationToken.None);

        (await context.Repository.GetMcpServerAsync("office/renderer"))!.IsEnabled.Should().BeFalse();

        await context.Service.SynchronizeMcpServersAsync(
            plugin with
            {
                AcknowledgedPermissionsJson = JsonSerializer.Serialize(new[] { "process.execute" })
            },
            CancellationToken.None);

        (await context.Repository.GetMcpServerAsync("office/renderer"))!.IsEnabled.Should().BeTrue();
    }

    [Fact]
    public async Task SynchronizeMcpServersAsync_preserves_user_values_and_drops_contributions_the_manifest_removed()
    {
        var context = CreateContext();
        var twoServers = """
            {
              "schemaVersion": 1,
              "id": "office",
              "name": "Office",
              "version": "1.0.0",
              "contributes": {
                "mcpServers": [
                  {
                    "id": "renderer",
                    "name": "Office Renderer",
                    "transport": "stdio",
                    "command": "node",
                    "arguments": [],
                    "requiredSettings": [{ "key": "LOG_LEVEL", "target": "env", "secret": false }]
                  },
                  {
                    "id": "legacy",
                    "name": "Legacy",
                    "transport": "stdio",
                    "command": "node",
                    "arguments": []
                  }
                ]
              }
            }
            """;
        var plugin = await CreatePluginAsync(context, twoServers);
        await context.Service.SynchronizeMcpServersAsync(plugin, CancellationToken.None);

        // Stand in for the user filling the placeholder in on the settings page.
        var configured = (await context.Repository.GetMcpServerAsync("office/renderer"))!;
        var configuredSettings = ExtensionCatalog.DeserializeSettings(configured.SettingsJson);
        _ = await context.Repository.UpsertMcpServerAsync(configured with
        {
            SettingsJson = ExtensionCatalog.SerializeSettings(configuredSettings with
            {
                Environment = new Dictionary<string, string> { ["LOG_LEVEL"] = "debug" }
            }),
            CredentialRefs = new Dictionary<string, string> { ["environment.STALE"] = "secret:stale" }
        });

        var legacyRemoved = await WritePluginManifestAsync(plugin, """
            {
              "schemaVersion": 1,
              "id": "office",
              "name": "Office",
              "version": "1.0.0",
              "contributes": {
                "mcpServers": [
                  {
                    "id": "renderer",
                    "name": "Office Renderer",
                    "transport": "stdio",
                    "command": "node",
                    "arguments": [],
                    "requiredSettings": [{ "key": "LOG_LEVEL", "target": "env", "secret": false }]
                  }
                ]
              }
            }
            """);

        await context.Service.SynchronizeMcpServersAsync(legacyRemoved, CancellationToken.None);

        var resynced = await context.Repository.GetMcpServerAsync("office/renderer");
        ExtensionCatalog.DeserializeSettings(resynced!.SettingsJson).Environment["LOG_LEVEL"]
            .Should().Be("debug");
        // A credential ref the manifest no longer declares is dropped and its secret deleted.
        resynced.CredentialRefs.Should().BeEmpty();
        context.SecretProtector.DeleteCalls.Should().Contain("secret:stale");
        (await context.Repository.GetMcpServerAsync("office/legacy")).Should().BeNull();
        context.McpClientManager.DrainedServerIds.Should().Contain("office/legacy");
    }

    [Fact]
    public async Task DeleteMcpServersAsync_drains_connections_before_removing_records_and_secrets()
    {
        var context = CreateContext();
        var plugin = await CreatePluginAsync(context, """
            {
              "schemaVersion": 1,
              "id": "office",
              "name": "Office",
              "version": "1.0.0",
              "contributes": {
                "mcpServers": [
                  {
                    "id": "renderer",
                    "name": "Office Renderer",
                    "transport": "stdio",
                    "command": "node",
                    "arguments": [],
                    "requiredSettings": [{ "key": "LICENSE_KEY", "target": "env", "secret": true }]
                  }
                ]
              }
            }
            """);
        await context.Service.SynchronizeMcpServersAsync(plugin, CancellationToken.None);
        var stored = (await context.Repository.GetMcpServerAsync("office/renderer"))!;
        _ = await context.Repository.UpsertMcpServerAsync(stored with
        {
            CredentialRefs = new Dictionary<string, string> { ["environment.LICENSE_KEY"] = "secret:license" }
        });

        await context.Service.DeleteMcpServersAsync("office", CancellationToken.None);

        (await context.Repository.GetMcpServerAsync("office/renderer")).Should().BeNull();
        context.SecretProtector.DeleteCalls.Should().Contain("secret:license");
        context.McpClientManager.DrainedServerIds.Should().Equal("office/renderer");
    }

    [Fact]
    public async Task SetMcpServersEnabledAsync_drains_only_when_disabling()
    {
        var context = CreateContext();
        var plugin = await CreatePluginAsync(context, """
            {
              "schemaVersion": 1,
              "id": "office",
              "name": "Office",
              "version": "1.0.0",
              "contributes": {
                "mcpServers": [
                  {
                    "id": "renderer",
                    "name": "Office Renderer",
                    "transport": "stdio",
                    "command": "node",
                    "arguments": []
                  }
                ]
              }
            }
            """);
        await context.Service.SynchronizeMcpServersAsync(plugin, CancellationToken.None);

        await context.Service.SetMcpServersEnabledAsync("office", true, CancellationToken.None);
        context.McpClientManager.DrainedServerIds.Should().BeEmpty();

        await context.Service.SetMcpServersEnabledAsync("office", false, CancellationToken.None);

        (await context.Repository.GetMcpServerAsync("office/renderer"))!.IsEnabled.Should().BeFalse();
        context.McpClientManager.DrainedServerIds.Should().Equal("office/renderer");
    }

    [Fact]
    public void EnsurePermissionsAcknowledged_rejects_a_permission_the_user_has_not_confirmed()
    {
        var context = CreateContext();
        var plugin = CreatePluginRecord(
            """{"schemaVersion":1,"id":"office","permissions":["workspace.write","process.execute"]}""",
            Path.Combine(_rootPath, "plugins", "office", "versions", "v1"),
            acknowledgedPermissionsJson: JsonSerializer.Serialize(new[] { "workspace.write" }));

        var action = () => context.Service.EnsurePermissionsAcknowledged(plugin);

        action.Should().Throw<InvalidOperationException>().WithMessage("*process.execute*");
    }

    [Fact]
    public void ListVersionDirectories_and_DeletePluginDirectory_reject_an_install_path_outside_the_plugin_layout()
    {
        var context = CreateContext();
        var plugin = CreatePluginRecord(
            """{"schemaVersion":1,"id":"office"}""",
            Path.Combine(_rootPath, "elsewhere", "office"));

        var list = () => context.Service.ListVersionDirectories(plugin);
        var delete = () => context.Service.DeletePluginDirectory(plugin);

        list.Should().Throw<InvalidOperationException>().WithMessage("*install path is invalid*");
        delete.Should().Throw<InvalidOperationException>().WithMessage("*install path is invalid*");
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

    private async Task<ExtensionPackageRecord> CreatePluginAsync(
        TestContext context,
        string manifestJson,
        bool isEnabled = true)
    {
        var installPath = Path.Combine(_rootPath, "plugins", "office", "versions", "v1");
        Directory.CreateDirectory(Path.Combine(installPath, "server"));
        await File.WriteAllTextAsync(Path.Combine(installPath, "server", "index.js"), "// fixture");
        await File.WriteAllTextAsync(
            Path.Combine(installPath, ExtensionInstallation.PluginManifestName),
            manifestJson);
        var plugin = CreatePluginRecord(manifestJson, installPath, isEnabled: isEnabled);
        return await context.Repository.UpsertPackageAsync(plugin);
    }

    private static async Task<ExtensionPackageRecord> WritePluginManifestAsync(
        ExtensionPackageRecord plugin,
        string manifestJson)
    {
        await File.WriteAllTextAsync(ExtensionInstallation.PluginManifestPath(plugin), manifestJson);
        return plugin with { ManifestJson = manifestJson };
    }

    private static ExtensionPackageRecord CreatePluginRecord(
        string manifestJson,
        string installPath,
        bool isEnabled = true,
        string? acknowledgedPermissionsJson = null)
    {
        var now = DateTimeOffset.UtcNow;
        return new ExtensionPackageRecord(
            ExtensionKind.Plugin,
            "office",
            "Office",
            "1.0.0",
            "Office workflows.",
            installPath,
            "sha256:fixture",
            manifestJson,
            null,
            isEnabled,
            acknowledgedPermissionsJson,
            acknowledgedPermissionsJson is null ? null : now,
            now,
            now);
    }

    private TestContext CreateContext()
    {
        var storagePaths = new StoragePaths(
            _rootPath,
            Path.Combine(_rootPath, "selfclaw.db"),
            Path.Combine(_rootPath, "secrets"));
        var repository = new SqliteExtensionRepository(new SqliteDatabase(storagePaths));
        var protector = new RecordingSecretProtector();
        var clientManager = new RecordingMcpClientManager();
        var limits = new ExtensionPackageLimits(
            100L * 1024 * 1024,
            300L * 1024 * 1024,
            5000,
            50L * 1024 * 1024,
            256L * 1024);
        return new TestContext(
            new PluginContributionService(
                repository,
                protector,
                clientManager,
                new PluginManifestReader(limits)),
            repository,
            protector,
            clientManager);
    }

    private sealed record TestContext(
        PluginContributionService Service,
        SqliteExtensionRepository Repository,
        RecordingSecretProtector SecretProtector,
        RecordingMcpClientManager McpClientManager);

    private sealed class RecordingMcpClientManager : IMcpClientManager
    {
        public List<string> DrainedServerIds { get; } = [];

        public Task<McpClientLease> AcquireAsync(
            ResolvedMcpServerConfiguration configuration,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<McpHealthResult> TestAsync(
            ResolvedMcpServerConfiguration configuration,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task DrainAsync(string serverId, CancellationToken cancellationToken = default)
        {
            DrainedServerIds.Add(serverId);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingSecretProtector : ISecretProtector
    {
        public List<string> DeleteCalls { get; } = [];

        public Task<string> StoreSecretAsync(
            string secret,
            string? existingSecretRef = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<string?> RetrieveSecretAsync(
            string secretRef,
            CancellationToken cancellationToken = default)
            => Task.FromResult<string?>(null);

        public Task DeleteSecretAsync(string secretRef, CancellationToken cancellationToken = default)
        {
            DeleteCalls.Add(secretRef);
            return Task.CompletedTask;
        }
    }
}
