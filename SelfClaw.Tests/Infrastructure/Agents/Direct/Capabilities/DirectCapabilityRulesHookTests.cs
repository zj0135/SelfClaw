using FluentAssertions;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Infrastructure.Agents.Direct.Capabilities;

namespace SelfClaw.Tests.Infrastructure.Agents.Direct.Capabilities;

public sealed class DirectCapabilityRulesHookTests : IDisposable
{
    private readonly string _rootPath = Path.Combine(
        Path.GetTempPath(),
        "SelfClawTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void CheckPackages_rejects_a_changed_inherited_hook_plugin()
    {
        var package = CreatePackage("alpha", enabled: true, hash: "hash-2");
        var ceiling = new DirectCapabilityCeiling(
            "system", [], [], [], [], [new DirectExtensionCapability("alpha", "1.0.0", "hash-1")]);

        var failure = DirectCapabilityRules.CheckPackages("system", [], [], ceiling, [package]);

        failure.Should().NotBeNull();
        failure!.ErrorCode.Should().Be(SubagentErrorCodes.CapabilityUnavailable);
    }

    [Fact]
    public void CheckPackages_accepts_a_current_inherited_hook_plugin()
    {
        var package = CreatePackage("alpha", enabled: true, hash: "hash-1");
        var ceiling = new DirectCapabilityCeiling(
            "system", [], [], [], [], [new DirectExtensionCapability("alpha", "1.0.0", "hash-1")]);

        DirectCapabilityRules.CheckPackages("system", [], [], ceiling, [package]).Should().BeNull();
    }

    [Fact]
    public void CheckPackages_treats_a_missing_hook_plugin_snapshot_as_empty()
    {
        var ceiling = new DirectCapabilityCeiling("system", [], [], [], []);

        DirectCapabilityRules.CheckPackages("system", [], [], ceiling, []).Should().BeNull();
    }

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, recursive: true);
        }
    }

    private ExtensionPackageRecord CreatePackage(string id, bool enabled, string hash)
    {
        var installPath = Path.Combine(_rootPath, id);
        Directory.CreateDirectory(installPath);
        File.WriteAllText(Path.Combine(installPath, "plugin.json"), "{}");
        return new ExtensionPackageRecord(
            ExtensionKind.Plugin,
            id,
            id,
            "1.0.0",
            "",
            installPath,
            hash,
            "{}",
            SourcePluginId: null,
            IsEnabled: enabled,
            AcknowledgedPermissionsJson: null,
            AcknowledgedAtUtc: null,
            InstalledAtUtc: DateTimeOffset.UnixEpoch,
            UpdatedAtUtc: DateTimeOffset.UnixEpoch);
    }
}
