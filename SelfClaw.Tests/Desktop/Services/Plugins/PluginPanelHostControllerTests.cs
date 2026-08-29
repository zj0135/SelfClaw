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

public sealed class PluginPanelHostControllerTests : IDisposable
{
    private readonly string _rootPath = Path.Combine(
        Path.GetTempPath(), "SelfClawTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task GetPanels_lists_only_enabled_panels_with_acknowledged_permissions()
    {
        var context = await CreateContextAsync();
        await context.InstallPluginAsync("git-inspector", enabled: true, acknowledged: true);
        await context.InstallPluginAsync("disabled-one", enabled: false, acknowledged: true);
        await context.InstallPluginAsync("unconfirmed", enabled: true, acknowledged: false);

        var response = await context.SendAsync("plugin-host/get-panels");

        var panels = response.GetProperty("panels").EnumerateArray().ToArray();
        panels.Should().ContainSingle();
        panels[0].GetProperty("key").GetString().Should().Be("git-inspector/changes");
        panels[0].GetProperty("origin").GetString()
            .Should().Be("https://git-inspector.plugin.selfclaw.local");
    }

    [Fact]
    public async Task Open_pins_the_version_directory_until_every_panel_of_the_plugin_closes()
    {
        var context = await CreateContextAsync();
        var installPath = await context.InstallPluginAsync("git-inspector", panelIds: ["changes", "log"]);

        await context.SendAsync("plugin-host/open", ("panelKey", "git-inspector/changes"));
        await context.SendAsync("plugin-host/open", ("panelKey", "git-inspector/log"));

        // A drain is what deleting the Plugin waits on, so "still open" has to mean "still blocked".
        context.TryDrain(installPath).Should().BeFalse();

        await context.SendAsync("plugin-host/close", ("panelKey", "git-inspector/changes"));
        context.TryDrain(installPath).Should().BeFalse();

        await context.SendAsync("plugin-host/close", ("panelKey", "git-inspector/log"));
        context.TryDrain(installPath).Should().BeTrue();
    }

    [Fact]
    public async Task Permissions_are_resolved_from_host_state_and_only_for_open_panels()
    {
        var context = await CreateContextAsync();
        await context.InstallPluginAsync("git-inspector");

        context.Controller.GetPermissions("git-inspector/changes").Should().BeNull();

        await context.SendAsync("plugin-host/open", ("panelKey", "git-inspector/changes"));

        context.Controller.GetPermissions("git-inspector/changes")
            .Should().Contain("host.workspace.read");
        context.Controller.GetPermissions("git-inspector/never-declared").Should().BeNull();
        context.Controller.GetPermissions("other-plugin/changes").Should().BeNull();
    }

    // Synchronous on purpose: CloseAsync marshals to the WPF dispatcher, and awaiting in xUnit can resume
    // the test on a different thread, which would leave the work queued on a dispatcher with no loop.
    // A sync test method has no ambient SynchronizationContext, so blocking here cannot deadlock.
#pragma warning disable xUnit1031
    [Fact]
    public void CloseAsync_releases_the_version_lease_and_tells_the_shell_to_drop_the_tab()
    {
        var context = CreateContextAsync().GetAwaiter().GetResult();
        var installPath = context.InstallPluginAsync("git-inspector").GetAwaiter().GetResult();
        context.SendAsync("plugin-host/open", ("panelKey", "git-inspector/changes")).GetAwaiter().GetResult();
        context.HostChannel.MarkReady();

        context.Controller.CloseAsync("git-inspector").GetAwaiter().GetResult();

        context.TryDrain(installPath).Should().BeTrue();
        var evictJson = context.PostedJson
            .Should().ContainSingle(json => json.Contains("plugin-host/evict", StringComparison.Ordinal)).Subject;
        using var evict = JsonDocument.Parse(evictJson);
        evict.RootElement.GetProperty("pluginId").GetString().Should().Be("git-inspector");
    }
#pragma warning restore xUnit1031

    [Fact]
    public async Task Open_rejects_a_panel_whose_plugin_is_not_enabled()
    {
        var context = await CreateContextAsync();
        await context.InstallPluginAsync("git-inspector", enabled: false);

        var response = await context.SendAsync("plugin-host/open", ("panelKey", "git-inspector/changes"));

        response.GetProperty("error").GetString().Should().Contain("not available");
    }

    [Fact]
    public async Task Saved_tabs_survive_a_new_controller_over_the_same_settings_file()
    {
        var context = await CreateContextAsync();
        await context.InstallPluginAsync("git-inspector");

        await context.SendAsync(
            "plugin-host/save-tabs",
            ("activeKey", "git-inspector/changes"),
            ("tabs", new[] { "git-inspector/changes" }));
        var restored = await context.SendAsync("plugin-host/get-panels");

        restored.GetProperty("tabs").EnumerateArray().Select(item => item.GetString())
            .Should().Equal("git-inspector/changes");
    }

    // The panel origin is only allowed to read what its own pinned version directory contains.
    [Theory]
    [InlineData("/ui/index.html", true)]
    [InlineData("/ui/../plugin.json", false)]
    [InlineData("/../../../secrets.txt", false)]
    [InlineData("/ui/./index.html", false)]
    [InlineData("/", false)]
    [InlineData("/ui/missing.html", false)]
    public void TryResolvePackageAsset_keeps_every_request_inside_the_pinned_version_directory(
        string requestPath,
        bool expected)
    {
        var packageRoot = Path.Combine(_rootPath, "package");
        Directory.CreateDirectory(Path.Combine(packageRoot, "ui"));
        File.WriteAllText(Path.Combine(packageRoot, "ui", "index.html"), "<!doctype html>");
        File.WriteAllText(Path.Combine(packageRoot, "plugin.json"), "{}");
        File.WriteAllText(Path.Combine(_rootPath, "secrets.txt"), "top secret");

        var resolved = PluginPanelHostController.TryResolvePackageAsset(packageRoot, requestPath, out var filePath);

        resolved.Should().Be(expected);
        if (expected)
        {
            filePath.Should().EndWith("index.html");
        }
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

    private Task<TestContext> CreateContextAsync() => TestContext.CreateAsync(_rootPath);

    private sealed class TestContext
    {
        private static readonly JsonSerializerOptions ResponseJsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        private readonly SqliteExtensionRepository _repository;
        private readonly PluginVersionLeaseManager _leaseManager;
        private readonly string _rootPath;

        private TestContext(
            string rootPath,
            SqliteExtensionRepository repository,
            PluginVersionLeaseManager leaseManager,
            PluginPanelHostController controller,
            WebViewHostChannel hostChannel,
            List<string> postedJson)
        {
            _rootPath = rootPath;
            _repository = repository;
            _leaseManager = leaseManager;
            Controller = controller;
            HostChannel = hostChannel;
            PostedJson = postedJson;
        }

        public PluginPanelHostController Controller { get; }

        public WebViewHostChannel HostChannel { get; }

        public List<string> PostedJson { get; }

        public static async Task<TestContext> CreateAsync(string rootPath)
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
            var catalog = new ExtensionCatalog(
                repository,
                repository,
                storagePaths,
                new PluginManifestReader(limits));
            var leaseManager = new PluginVersionLeaseManager();
            var hostChannel = new WebViewHostChannel();
            var postedJson = new List<string>();
            hostChannel.Attach(postedJson.Add);
            var controller = new PluginPanelHostController(
                catalog,
                repository,
                leaseManager,
                new DesktopSettingsJsonStore(storagePaths),
                hostChannel,
                Dispatcher.CurrentDispatcher);
            return new TestContext(rootPath, repository, leaseManager, controller, hostChannel, postedJson);
        }

        public async Task<string> InstallPluginAsync(
            string pluginId,
            bool enabled = true,
            bool acknowledged = true,
            IReadOnlyList<string>? panelIds = null)
        {
            var ids = panelIds ?? ["changes"];
            var installPath = Path.Combine(_rootPath, "plugins", pluginId, "versions", "v1");
            Directory.CreateDirectory(Path.Combine(installPath, "ui"));
            foreach (var id in ids)
            {
                await File.WriteAllTextAsync(Path.Combine(installPath, "ui", $"{id}.html"), "<!doctype html>");
            }

            var panels = string.Join(',', ids.Select(id =>
                $$"""{"id":"{{id}}","title":"{{id}}","entry":"ui/{{id}}.html"}"""));
            var manifest = $$$"""
                {"schemaVersion":1,"id":"{{{pluginId}}}","name":"{{{pluginId}}}","version":"1.0.0",
                 "permissions":["ui.panel","host.workspace.read"],
                 "contributes":{"panels":[{{{panels}}}]}}
                """;
            await File.WriteAllTextAsync(Path.Combine(installPath, "plugin.json"), manifest);
            var now = DateTimeOffset.UtcNow;
            await _repository.UpsertPackageAsync(new ExtensionPackageRecord(
                ExtensionKind.Plugin,
                pluginId,
                pluginId,
                "1.0.0",
                string.Empty,
                installPath,
                "sha256:v1",
                manifest,
                null,
                enabled,
                acknowledged ? """["host.workspace.read","ui.panel"]""" : null,
                acknowledged ? now : null,
                now,
                now));
            return installPath;
        }

        public async Task<JsonElement> SendAsync(string type, params (string Key, object Value)[] fields)
        {
            var payload = new Dictionary<string, object> { ["type"] = type };
            foreach (var (key, value) in fields)
            {
                payload[key] = value;
            }

            var response = await Controller.TryHandleAsync(
                type,
                JsonDocument.Parse(JsonSerializer.Serialize(payload)).RootElement);
            // Matches WebViewHostChannel, which is what actually serializes these responses in production.
            return JsonDocument.Parse(JsonSerializer.Serialize(response, ResponseJsonOptions)).RootElement;
        }

        public bool TryDrain(string installPath)
            => _leaseManager.DrainAsync(installPath).Wait(TimeSpan.FromMilliseconds(250));
    }
}
