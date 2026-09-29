using FluentAssertions;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Core.Runtime.Agent;
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

    [Theory]
    [InlineData(AgentRuntimeDefinition.SystemToolPolicy, AgentRuntimeDefinition.SystemToolPolicy, true)]
    [InlineData(AgentRuntimeDefinition.ReadOnlyToolPolicy, AgentRuntimeDefinition.SystemToolPolicy, true)]
    [InlineData(AgentRuntimeDefinition.NoneToolPolicy, AgentRuntimeDefinition.SystemToolPolicy, true)]
    [InlineData(AgentRuntimeDefinition.NoneToolPolicy, AgentRuntimeDefinition.ReadOnlyToolPolicy, true)]
    [InlineData(AgentRuntimeDefinition.SystemToolPolicy, AgentRuntimeDefinition.ReadOnlyToolPolicy, false)]
    [InlineData(AgentRuntimeDefinition.ReadOnlyToolPolicy, AgentRuntimeDefinition.NoneToolPolicy, false)]
    public void IsToolPolicyAuthorized_compares_policy_rank(string requested, string ceiling, bool expected)
        => DirectCapabilityRules.IsToolPolicyAuthorized(requested, ceiling).Should().Be(expected);

    [Fact]
    public void An_unknown_tool_policy_is_a_definition_bug_rather_than_a_silent_deny()
    {
        // 'readonly' is the plausible typo this guards against: silently treating it as unauthorized
        // (or as a ceiling) would look like capabilities vanishing instead of a broken definition.
        const string unknown = "readonly";

        var asRequested = () => DirectCapabilityRules.IsToolPolicyAuthorized(unknown, AgentRuntimeDefinition.SystemToolPolicy);
        var asCeiling = () => DirectCapabilityRules.IsToolPolicyAuthorized(AgentRuntimeDefinition.SystemToolPolicy, unknown);
        var asPolicy = () => DirectCapabilityRules.Allows(ToolCallKind.Run, unknown);

        asRequested.Should().Throw<InvalidDataException>().WithMessage("*'readonly'*");
        asCeiling.Should().Throw<InvalidDataException>().WithMessage("*'readonly'*");
        asPolicy.Should().Throw<InvalidDataException>().WithMessage("*'readonly'*");
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
