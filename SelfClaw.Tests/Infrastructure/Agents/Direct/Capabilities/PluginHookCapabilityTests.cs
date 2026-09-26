using System.Text.Json;
using FluentAssertions;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Infrastructure.Agents.Direct.Capabilities;
using SelfClaw.Infrastructure.Agents.Direct.Capabilities.Models;
using SelfClaw.Infrastructure.Agents.Direct.Hooks;
using SelfClaw.Infrastructure.Extensions;
using SelfClaw.Infrastructure.Extensions.Models;
using SelfClaw.Infrastructure.Extensions.Plugins;
using SelfClaw.Infrastructure.Extensions.Skills;

namespace SelfClaw.Tests.Infrastructure.Agents.Direct.Capabilities;

public sealed class PluginHookCapabilityTests : IDisposable
{
    private readonly string _rootPath = Path.Combine(
        Path.GetTempPath(),
        "SelfClawTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Own_hooks_are_ordered_by_plugin_id_then_declaration_order()
    {
        var alpha = await CreatePluginAsync("alpha", Hooks(Hook("z-first"), Hook("a-second")));
        var beta = await CreatePluginAsync("beta", Hooks(Hook("only")));

        var capabilities = await ResolveAsync([beta, alpha], ["alpha", "beta"]);

        capabilities.Hooks.Select(item => $"{item.PluginId}/{item.Contribution.Id}")
            .Should().Equal("alpha/z-first", "alpha/a-second", "beta/only");
        capabilities.Hooks.Should().OnlyContain(item => !item.Inherited);
        capabilities.HookBlockReason.Should().BeNull();
    }

    [Fact]
    public async Task A_skipped_plugin_with_hooks_produces_a_notice_and_no_hooks()
    {
        var plugin = await CreatePluginAsync("alpha", Hooks(Hook("a")), acknowledgePermissions: false);

        var capabilities = await ResolveAsync([plugin], ["alpha"]);

        capabilities.Hooks.Should().BeEmpty();
        capabilities.HookNotices.Should().ContainSingle().Which.Should().Contain("declares hooks but was skipped");
    }

    [Fact]
    public async Task A_skipped_plugin_without_hooks_produces_no_notice()
    {
        var plugin = await CreatePluginAsync("alpha", contributions: "", acknowledgePermissions: false);

        var capabilities = await ResolveAsync([plugin], ["alpha"]);

        capabilities.HookNotices.Should().BeEmpty();
    }

    [Fact]
    public async Task An_inherited_hook_plugin_contributes_hooks_only()
    {
        var plugin = await CreatePluginAsync(
            "alpha",
            Hooks(Hook("a")),
            extraContributions: "\"directInstructions\":\"instructions.md\",");

        var capabilities = await ResolveAsync(
            [plugin],
            pluginIds: [],
            inheritedHookPlugins: [new DirectExtensionCapability(plugin.Id, plugin.Version, plugin.ContentHash)]);

        capabilities.Hooks.Should().ContainSingle().Which.Inherited.Should().BeTrue();
        capabilities.PluginRoots.Should().BeEmpty();
        capabilities.Instructions.Should().BeEmpty();
        capabilities.HookBlockReason.Should().BeNull();
    }

    [Fact]
    public async Task An_inherited_hook_plugin_bound_again_stays_not_inherited()
    {
        var plugin = await CreatePluginAsync("alpha", Hooks(Hook("a")));

        var capabilities = await ResolveAsync(
            [plugin],
            pluginIds: ["alpha"],
            inheritedHookPlugins: [new DirectExtensionCapability(plugin.Id, plugin.Version, plugin.ContentHash)]);

        capabilities.Hooks.Should().ContainSingle().Which.Inherited.Should().BeFalse();
    }

    [Theory]
    [InlineData("deleted")]
    [InlineData("disabled")]
    [InlineData("changed")]
    public async Task An_unavailable_inherited_hook_plugin_blocks_the_turn(string change)
    {
        var plugin = await CreatePluginAsync("alpha", Hooks(Hook("a")));
        var captured = new DirectExtensionCapability(plugin.Id, plugin.Version, plugin.ContentHash);
        var packages = new List<ExtensionPackageRecord> { plugin };
        switch (change)
        {
            case "deleted":
                File.Delete(Path.Combine(plugin.InstallPath, "plugin.json"));
                break;
            case "disabled":
                packages[0] = plugin with { IsEnabled = false };
                break;
            default:
                packages[0] = plugin with { ContentHash = "changed" };
                break;
        }

        var capabilities = await ResolveAsync(packages, pluginIds: [], inheritedHookPlugins: [captured]);

        capabilities.HookBlockReason.Should().Be(
            "Inherited hook plugin 'alpha' is unavailable or changed since delegation; the turn was blocked.");
        capabilities.Hooks.Should().BeEmpty();
    }

    [Fact]
    public async Task An_inherited_hook_plugin_whose_permissions_are_unconfirmed_blocks_the_turn()
    {
        var plugin = await CreatePluginAsync("alpha", Hooks(Hook("a")), acknowledgePermissions: false);

        var capabilities = await ResolveAsync(
            [plugin],
            pluginIds: [],
            inheritedHookPlugins: [new DirectExtensionCapability(plugin.Id, plugin.Version, plugin.ContentHash)]);

        capabilities.HookBlockReason.Should().NotBeNull();
    }

    [Fact]
    public async Task A_null_hook_plugin_snapshot_inherits_nothing()
    {
        var plugin = await CreatePluginAsync("alpha", Hooks(Hook("a")));

        var capabilities = await ResolveAsync([plugin], pluginIds: [], inheritedHookPlugins: []);

        capabilities.Hooks.Should().BeEmpty();
        capabilities.HookBlockReason.Should().BeNull();
    }

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, recursive: true);
        }
    }

    private async Task<PluginCapabilities> ResolveAsync(
        IReadOnlyList<ExtensionPackageRecord> packages,
        IReadOnlyList<string> pluginIds,
        IReadOnlyList<DirectExtensionCapability>? inheritedHookPlugins = null)
    {
        var limits = new ExtensionPackageLimits(1024 * 1024, 1024 * 1024, 100, 512 * 1024, 256 * 1024);
        var source = new PluginCapabilitySource(
            new PluginManifestReader(limits),
            new SkillPackageReader(limits),
            new PluginVersionLeaseManager(),
            new CapabilityContentCache());
        var agent = new AgentRuntimeDefinition(
            "build", "Build", "", AgentExecutionMode.Direct,
            AgentRuntimeDefinition.SystemToolPolicy, pluginIds, [], [], [], "");
        await using var leases = new DirectTurnLeaseScope();
        return await source.ResolveAsync(
            agent,
            packages,
            new Dictionary<string, ExtensionPackageRecord>(),
            inheritedHookPlugins ?? [],
            leases,
            new TurnDiagnostics(),
            CancellationToken.None);
    }

    private async Task<ExtensionPackageRecord> CreatePluginAsync(
        string id,
        string contributions = "",
        bool acknowledgePermissions = true,
        string extraContributions = "")
    {
        var installPath = Path.Combine(_rootPath, id);
        Directory.CreateDirectory(installPath);
        var manifest = $$"""
            {
              "schemaVersion": 1,
              "id": "{{id}}",
              "name": "{{id}}",
              "version": "1.0.0",
              "permissions": ["hooks.run", "hooks.tool"],
              "contributes": { {{extraContributions}} {{contributions}} }
            }
            """;
        await File.WriteAllTextAsync(Path.Combine(installPath, "plugin.json"), manifest);
        if (extraContributions.Contains("instructions.md", StringComparison.Ordinal))
        {
            await File.WriteAllTextAsync(Path.Combine(installPath, "instructions.md"), "plugin instructions");
        }

        var now = DateTimeOffset.UtcNow;
        return new ExtensionPackageRecord(
            ExtensionKind.Plugin,
            id,
            id,
            "1.0.0",
            "",
            installPath,
            "hash-1",
            manifest,
            SourcePluginId: null,
            IsEnabled: true,
            AcknowledgedPermissionsJson: acknowledgePermissions
                ? JsonSerializer.Serialize(new[] { "hooks.run", "hooks.tool" })
                : null,
            AcknowledgedAtUtc: now,
            InstalledAtUtc: now,
            UpdatedAtUtc: now);
    }

    private static string Hooks(params string[] hooks)
        => "\"hooks\":[" + string.Join(",", hooks) + "]";

    private static string Hook(string id)
        => $$"""{ "id": "{{id}}", "event": "runStarting", "command": "node" }""";
}
