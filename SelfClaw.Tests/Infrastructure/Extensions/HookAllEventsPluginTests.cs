using FluentAssertions;
using SelfClaw.Infrastructure.Extensions.Models;
using SelfClaw.Infrastructure.Extensions.Plugins;
using SelfClaw.Infrastructure.Extensions.Plugins.Models;

namespace SelfClaw.Tests.Infrastructure.Extensions;

/// <summary>
/// Validates the checked-in all-events example against the real manifest reader, so the documented
/// coverage of the six Direct hook events cannot drift away from the rules.
/// </summary>
public sealed class HookAllEventsPluginTests
{
    [Fact]
    public async Task The_all_events_example_passes_manifest_validation_for_every_event()
    {
        var pluginRoot = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "../../../../", "plugins", "hook-all-events"));
        Directory.Exists(pluginRoot).Should().BeTrue("the all-events example is checked into the repository");

        var reader = new PluginManifestReader(new ExtensionPackageLimits(
            100L * 1024 * 1024, 300L * 1024 * 1024, 5000, 50L * 1024 * 1024, 256L * 1024));

        var manifest = await reader.ReadAsync(Path.Combine(pluginRoot, "plugin.json"));

        manifest.Id.Should().Be("hook-all-events");
        manifest.Permissions.Should().Equal("hooks.http", "hooks.http.body", "hooks.run", "hooks.tool");
        manifest.Contributions.Hooks.Select(hook => hook.Event).Distinct()
            .Should().BeEquivalentTo(Enum.GetValues<PluginHookEvent>());
        manifest.Contributions.Hooks.Should().OnlyContain(hook => hook.Command == "powershell.exe");

        var runStarting = Single(manifest, "run-starting");
        runStarting.Event.Should().Be(PluginHookEvent.RunStarting);
        runStarting.OnFailure.Should().Be(PluginHookFailurePolicy.Continue);
        runStarting.Matcher.Origins.Should().HaveCount(3);

        var runCompleted = Single(manifest, "run-completed");
        runCompleted.Event.Should().Be(PluginHookEvent.RunCompleted);
        runCompleted.RunAsync.Should().BeFalse();

        var toolExecuting = Single(manifest, "tool-executing");
        toolExecuting.Event.Should().Be(PluginHookEvent.ToolExecuting);
        toolExecuting.OnFailure.Should().Be(PluginHookFailurePolicy.Block);
        toolExecuting.Matcher.ToolPatterns.Should().Equal("run_shell_command");
        toolExecuting.Matcher.Kinds.Should().Equal(SelfClaw.Core.Runtime.Agent.ToolCallKind.Run);

        var feedback = Single(manifest, "tool-executed-feedback");
        feedback.Event.Should().Be(PluginHookEvent.ToolExecuted);
        feedback.RunAsync.Should().BeFalse();

        var audit = Single(manifest, "tool-executed-audit");
        audit.Event.Should().Be(PluginHookEvent.ToolExecuted);
        audit.RunAsync.Should().BeTrue();
        audit.Matcher.Sources.Should().HaveCount(4);

        var readFileFeedback = Single(manifest, "read-file-feedback");
        readFileFeedback.Event.Should().Be(PluginHookEvent.ToolExecuted);
        readFileFeedback.RunAsync.Should().BeFalse();
        readFileFeedback.Matcher.ToolPatterns.Should().Equal("read_file");

        var httpRequest = Single(manifest, "http-request-sending");
        httpRequest.Event.Should().Be(PluginHookEvent.HttpRequestSending);
        httpRequest.IncludeRequestBody.Should().BeTrue();

        var httpResponse = Single(manifest, "http-response-received");
        httpResponse.Event.Should().Be(PluginHookEvent.HttpResponseReceived);
        httpResponse.Matcher.HostPatterns.Should().BeEmpty();
    }

    private static PluginHookContribution Single(PluginManifest manifest, string hookId)
        => manifest.Contributions.Hooks.Should().ContainSingle(hook => hook.Id == hookId).Subject;
}
