using System.ComponentModel;
using System.Text.Json;
using System.Windows.Threading;
using FluentAssertions;
using SelfClaw.Core.Models;
using SelfClaw.Desktop.Services;
using SelfClaw.Desktop.Services.Plugins;
using SelfClaw.Desktop.Services.WebView;
using SelfClaw.Infrastructure.Data.Sqlite;
using SelfClaw.Infrastructure.Data.Sqlite.Repositories;
using SelfClaw.Infrastructure.Extensions;
using SelfClaw.Infrastructure.Extensions.Models;
using SelfClaw.Infrastructure.Extensions.Plugins;
using SelfClaw.Infrastructure.Options;

namespace SelfClaw.Tests.Desktop.Services.Plugins;

/// <summary>
/// The publisher exists so that a panel's pushed context and its pulled context are the same object.
/// These tests hold that line, and cover the two cases where "push only on change" is not enough: a
/// panel that just opened, and a shell that was not listening yet.
/// </summary>
/// <remarks>
/// Synchronous on purpose, like <see cref="PluginPanelHostControllerTests"/>: the publisher marshals to
/// the WPF dispatcher, and an awaiting xUnit test can resume on a different thread, which would leave
/// every push queued on a dispatcher that has no loop to run it. A sync test method has no ambient
/// SynchronizationContext, so blocking here cannot deadlock.
/// </remarks>
#pragma warning disable xUnit1031
public sealed class PluginPanelContextPublisherTests : IDisposable
{
    private readonly string _rootPath = Path.Combine(
        Path.GetTempPath(), "SelfClawTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void The_pushed_context_is_the_same_context_getContext_returns()
    {
        var context = CreateContext();
        context.HostChannel.MarkReady();
        context.Source.Change(new PluginPanelContext(
            "11111111-1111-1111-1111-111111111111",
            "agent-a",
            "Agent A",
            "direct",
            true,
            @"D:\work\repo",
            "repo"));

        var pushed = context.PushedContexts.Should().ContainSingle().Subject;

        // Same fields, same values, same casing — the divergence this class was written to remove.
        pushed.GetProperty("conversationId").GetString().Should().Be("11111111-1111-1111-1111-111111111111");
        pushed.GetProperty("agentId").GetString().Should().Be("agent-a");
        pushed.GetProperty("agentName").GetString().Should().Be("Agent A");
        pushed.GetProperty("agentMode").GetString().Should().Be("direct");
        pushed.GetProperty("isBusy").GetBoolean().Should().BeTrue();
        pushed.GetProperty("workspaceRootPath").GetString().Should().Be(@"D:\work\repo");
        pushed.GetProperty("workspaceRootName").GetString().Should().Be("repo");
        context.Publisher.Capture().Should().Be(context.Source.Context);
    }

    [Fact]
    public void A_transcript_publish_that_changes_nothing_a_panel_can_see_is_not_pushed()
    {
        var context = CreateContext();
        context.HostChannel.MarkReady();
        context.Source.Change(context.Source.Context with { IsBusy = true });
        context.PushedContexts.Should().ContainSingle();

        // Streaming republishes the transcript every 120ms; none of that reaches a panel on its own.
        context.PublishTranscript();
        context.PublishTranscript();

        context.PushedContexts.Should().ContainSingle();
    }

    [Fact]
    public void A_busy_change_arrives_through_the_transcript_publish()
    {
        var context = CreateContext();
        context.HostChannel.MarkReady();

        // IsBusy has no property notification of its own — the transcript publish is its only signal.
        context.Source.Context = context.Source.Context with { IsBusy = true };
        context.PublishTranscript();

        context.PushedContexts.Should().ContainSingle()
            .Which.GetProperty("isBusy").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public void Opening_a_panel_pushes_the_context_even_when_it_has_not_changed()
    {
        var context = CreateContext();
        context.HostChannel.MarkReady();
        context.InstallPlugin("git-inspector");
        context.Source.Change(context.Source.Context with { WorkspaceRootName = "repo" });
        context.PushedContexts.Should().ContainSingle();

        var opened = context.OpenPanel("git-inspector/changes");

        opened.TryGetProperty("error", out var error).Should().BeFalse(error.ToString());
        // The panel missed the first push entirely; deduplication must not leave it with nothing.
        context.PushedContexts.Should().HaveCount(2);
        context.PushedContexts[1].GetProperty("workspaceRootName").GetString().Should().Be("repo");
    }

    [Fact]
    public void A_context_the_shell_never_received_is_not_treated_as_delivered()
    {
        var context = CreateContext();

        // The shell has not reported ready, so this push goes nowhere.
        context.Source.Change(context.Source.Context with { AgentId = "agent-b" });
        context.PushedContexts.Should().BeEmpty();

        context.HostChannel.MarkReady();
        context.PublishTranscript();

        context.PushedContexts.Should().ContainSingle()
            .Which.GetProperty("agentId").GetString().Should().Be("agent-b");
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

    // Captured on the test thread before anything blocks, so the dispatcher the publisher marshals to is
    // the one this thread can pump. Reading it inside the async setup would bind to whichever pool thread
    // happened to resume there, and every queued push would be stranded.
    private TestContext CreateContext()
        => TestContext.CreateAsync(_rootPath, Dispatcher.CurrentDispatcher).GetAwaiter().GetResult();

    private sealed class FakeContextSource : IPluginPanelContextSource
    {
        public PluginPanelContext Context { get; set; } =
            new(null, "agent-a", "Agent A", "cli", false, null, null);

        public event PropertyChangedEventHandler? PropertyChanged;

        public PluginPanelContext CaptureContext() => Context;

        public void Change(PluginPanelContext context)
        {
            Context = context;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Context)));
        }
    }

    private sealed class TestContext
    {
        private readonly SqliteExtensionRepository _repository;
        private readonly string _rootPath;
        private readonly List<string> _postedJson;

        private TestContext(
            string rootPath,
            SqliteExtensionRepository repository,
            PluginPanelHostController controller,
            PluginPanelContextPublisher publisher,
            FakeContextSource source,
            WebViewHostChannel hostChannel,
            List<string> postedJson)
        {
            _rootPath = rootPath;
            _repository = repository;
            _postedJson = postedJson;
            Controller = controller;
            Publisher = publisher;
            Source = source;
            HostChannel = hostChannel;
        }

        public PluginPanelHostController Controller { get; }

        public PluginPanelContextPublisher Publisher { get; }

        public FakeContextSource Source { get; }

        public WebViewHostChannel HostChannel { get; }

        /// <summary>Every `plugin-host/context` push, in order, as the shell would parse it.</summary>
        public IReadOnlyList<JsonElement> PushedContexts => _postedJson
            .Select(json => JsonDocument.Parse(json).RootElement)
            .Where(element =>
                element.TryGetProperty("type", out var type) &&
                type.GetString() == "plugin-host/context")
            .Select(element => element.GetProperty("context"))
            .ToArray();

        public static async Task<TestContext> CreateAsync(string rootPath, Dispatcher dispatcher)
        {
            Directory.CreateDirectory(rootPath);
            var storagePaths = new StoragePaths(
                rootPath,
                Path.Combine(rootPath, "selfclaw.db"),
                Path.Combine(rootPath, "secrets"));
            var repository = new SqliteExtensionRepository(new SqliteDatabase(storagePaths));
            await repository.InitializeAsync();
            var limits = new ExtensionPackageLimits(
                100L * 1024 * 1024, 300L * 1024 * 1024, 5000, 50L * 1024 * 1024, 256L * 1024);
            var hostChannel = new WebViewHostChannel();
            var postedJson = new List<string>();
            hostChannel.Attach(postedJson.Add);
            var controller = new PluginPanelHostController(
                new ExtensionCatalog(repository, repository, storagePaths, new PluginManifestReader(limits)),
                repository,
                new PluginVersionLeaseManager(),
                new DesktopSettingsJsonStore(storagePaths),
                hostChannel,
                dispatcher);
            var source = new FakeContextSource();
            var publisher = new PluginPanelContextPublisher(
                source,
                hostChannel,
                controller,
                dispatcher);
            return new TestContext(rootPath, repository, controller, publisher, source, hostChannel, postedJson);
        }

        public void PublishTranscript()
            => HostChannel.PublishTranscript(new TranscriptRenderState(
                [],
                false,
                [],
                null,
                Source.Context.IsBusy));

        public JsonElement OpenPanel(string panelKey)
        {
            var response = Controller.TryHandleAsync(
                    "plugin-host/open",
                    JsonDocument.Parse($$"""{"type":"plugin-host/open","panelKey":"{{panelKey}}"}""").RootElement)
                .GetAwaiter()
                .GetResult();
            // The open completes on a pool thread, so the publisher marshals its push back to the
            // dispatcher. Production has a running UI loop; here the queue has to be pumped by hand.
            DrainDispatcher();
            return JsonDocument
                .Parse(JsonSerializer.Serialize(
                    response,
                    new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }))
                .RootElement;
        }

        private static void DrainDispatcher()
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.InvokeAsync(
                () => frame.Continue = false,
                DispatcherPriority.Background);
            Dispatcher.PushFrame(frame);
        }

        public void InstallPlugin(string pluginId)
        {
            var installPath = Path.Combine(_rootPath, "plugins", pluginId, "versions", "v1");
            Directory.CreateDirectory(Path.Combine(installPath, "ui"));
            File.WriteAllText(Path.Combine(installPath, "ui", "changes.html"), "<!doctype html>");
            var manifest = $$$"""
                {"schemaVersion":1,"id":"{{{pluginId}}}","name":"{{{pluginId}}}","version":"1.0.0",
                 "permissions":["ui.panel","host.context.read"],
                 "contributes":{"panels":[{"id":"changes","title":"changes","entry":"ui/changes.html"}]}}
                """;
            File.WriteAllText(Path.Combine(installPath, "plugin.json"), manifest);
            var now = DateTimeOffset.UtcNow;
            _repository.UpsertPackageAsync(new ExtensionPackageRecord(
                ExtensionKind.Plugin,
                pluginId,
                pluginId,
                "1.0.0",
                string.Empty,
                installPath,
                "sha256:v1",
                manifest,
                null,
                true,
                """["host.context.read","ui.panel"]""",
                now,
                now,
                now)).GetAwaiter().GetResult();
        }
    }
}
#pragma warning restore xUnit1031
