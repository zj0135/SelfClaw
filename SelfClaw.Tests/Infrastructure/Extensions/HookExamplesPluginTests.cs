using FluentAssertions;
using SelfClaw.Infrastructure.Extensions.Models;
using SelfClaw.Infrastructure.Extensions.Plugins;
using SelfClaw.Infrastructure.Extensions.Plugins.Models;

namespace SelfClaw.Tests.Infrastructure.Extensions;

/// <summary>
/// Validates the checked-in example plugin through the real manifest reader, so a rule change that
/// invalidates the documented example fails here instead of during a manual walkthrough.
/// </summary>
public sealed class HookExamplesPluginTests
{
    [Fact]
    public async Task The_example_hook_plugin_passes_manifest_validation()
    {
        var pluginRoot = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "../../../../", "plugins", "hook-examples"));
        Directory.Exists(pluginRoot).Should().BeTrue("the example plugin is checked into the repository");

        var reader = new PluginManifestReader(new ExtensionPackageLimits(
            100L * 1024 * 1024, 300L * 1024 * 1024, 5000, 50L * 1024 * 1024, 256L * 1024));

        var manifest = await reader.ReadAsync(Path.Combine(pluginRoot, "plugin.json"));

        manifest.Id.Should().Be("hook-examples");
        manifest.Permissions.Should().Equal("hooks.run", "hooks.tool");
        manifest.Contributions.Hooks.Should().HaveCount(2);
        manifest.Contributions.Hooks.Select(hook => hook.Id).Should().Equal("guard-shell", "audit-run");

        var guard = manifest.Contributions.Hooks[0];
        guard.Event.Should().Be(PluginHookEvent.ToolExecuting);
        guard.OnFailure.Should().Be(PluginHookFailurePolicy.Block);
        guard.Matcher.ToolPatterns.Should().Equal("run_shell_command");
        guard.Command.Should().Be("powershell.exe");
        guard.Arguments.Should().Contain(argument => argument.StartsWith("${pluginRoot}/hooks/", StringComparison.Ordinal));

        var audit = manifest.Contributions.Hooks[1];
        audit.Event.Should().Be(PluginHookEvent.RunCompleted);
        audit.RunAsync.Should().BeFalse();
        audit.Matcher.ToolPatterns.Should().BeEmpty();
    }
}
