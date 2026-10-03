using FluentAssertions;
using SelfClaw.Tests.TestDoubles;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Infrastructure.Extensions.Models;
using SelfClaw.Infrastructure.Extensions.Plugins;

namespace SelfClaw.Tests.Infrastructure.Extensions;

/// <summary>
/// Validates the checked-in example plugin through the real manifest reader, so a rule change that
/// invalidates the documented example fails here instead of during a manual walkthrough.
/// </summary>
public sealed class ViewDemoPluginTests
{
    [Fact]
    public async Task The_example_view_plugin_passes_manifest_validation()
    {
        var pluginRoot = TestRepositoryRoot.GetPath("plugins", "view-demo");
        Directory.Exists(pluginRoot).Should().BeTrue("the example plugin is checked into the repository");

        var reader = new PluginManifestReader(new ExtensionPackageLimits(
            100L * 1024 * 1024, 300L * 1024 * 1024, 5000, 50L * 1024 * 1024, 256L * 1024));

        var manifest = await reader.ReadAsync(Path.Combine(pluginRoot, "plugin.json"));

        manifest.Id.Should().Be("view-demo");
        manifest.Permissions.Should().Contain(PluginPermissions.Panel).And.Contain(PluginPermissions.Floating);
        manifest.Contributions.Views.Should().HaveCount(2);

        var docked = manifest.Contributions.Views.Single(view => view.Slot == PluginViewSlot.Right);
        docked.Id.Should().Be("inspector");
        docked.Entry.Should().Be("ui/dock.html");
        docked.DefaultWidth.Should().Be(420);

        var floating = manifest.Contributions.Views.Single(view => view.Slot == PluginViewSlot.Floating);
        floating.Id.Should().Be("hud");
        floating.Entry.Should().Be("ui/hud.html");
        floating.DefaultWidth.Should().BeNull();
    }
}
