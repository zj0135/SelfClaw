using SelfClaw.Desktop.Services.Settings;
using System.Text.Json;
using System.Windows.Threading;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Desktop.Services.Plugins;
using SelfClaw.Desktop.Services.WebView;
using SelfClaw.Infrastructure.Extensions;
using SelfClaw.Infrastructure.Options;
using SelfClaw.Tests.TestDoubles;

namespace SelfClaw.Tests.Desktop.Services.Plugins;

public sealed class PluginPanelConcurrencyTests
{
    [Fact]
    public Task Concurrent_opens_share_one_version_and_the_last_close_releases_it() => WpfDispatcherTest.RunAsync(async () =>
    {
        using var context = new Context();
        var first = context.OpenAsync("one");
        await context.Catalog.Requested.Task;
        var second = context.OpenAsync("two");
        context.Catalog.Release.SetResult();
        (await first).TryGetProperty("error", out _).Should().BeFalse();
        (await second).TryGetProperty("error", out _).Should().BeFalse();
        context.Catalog.Reads.Should().Be(1);
        await context.CloseTabAsync("one");
        context.Host.GetPermissions("plugin/two").Should().NotBeNull();
        await context.CloseTabAsync("two");
        using var drain = await context.Leases.AcquireDrainsAsync([context.Path], CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
        context.Host.GetPermissions("plugin/two").Should().BeNull();
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Closing_or_disposing_during_acquisition_invalidates_the_pending_result(bool dispose)
        => WpfDispatcherTest.RunAsync(async () =>
        {
            using var context = new Context();
            var opening = context.OpenAsync("one");
            await context.Catalog.Requested.Task;
            if (dispose) context.Host.Dispose();
            else await context.Host.CloseAsync("plugin");
            context.Catalog.Release.SetResult();
            (await opening).GetProperty("error").GetString().Should().NotBeNullOrEmpty();
            using var drain = await context.Leases.AcquireDrainsAsync([context.Path], CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
            context.Host.GetPermissions("plugin/one").Should().BeNull();
        });

    [Fact]
    public async Task Resource_reads_return_explicit_http_errors_and_enforce_a_byte_budget()
    {
        using var context = new Context();
        var reader = new PluginPanelResourceReader(NullLogger<PluginPanelResourceReader>.Instance);
        var file = System.IO.Path.Combine(context.Path, "large.bin");
        await using (var stream = File.Create(file)) stream.SetLength(PluginPanelResourceReader.MaximumResourceBytes + 1L);
        (await reader.ReadAsync(context.Path, "/large.bin", "default-src 'self'")).StatusCode.Should().Be(413);
        (await reader.ReadAsync(context.Path, "/missing", "default-src 'self'")).StatusCode.Should().Be(404);
        var content = System.IO.Path.Combine(context.Path, "index.html");
        await File.WriteAllTextAsync(content, "hello");
        using (var locked = new FileStream(content, FileMode.Open, FileAccess.Read, FileShare.None))
            (await reader.ReadAsync(context.Path, "/index.html", "default-src 'self'")).StatusCode.Should().Be(500);
        var success = await reader.ReadAsync(context.Path, "/index.html", "default-src 'self'");
        success.StatusCode.Should().Be(200);
        success.Headers.Should().Contain("Content-Security-Policy: default-src 'self'").And.Contain("nosniff");
    }

    private sealed class Context : IDisposable
    {
        public Context()
        {
            Directory.CreateDirectory(Path);
            Catalog = new ControlledCatalog(Path);
            Host = new PluginPanelHostController(Catalog, Catalog, Leases,
                new DesktopSettingsJsonStore(StoragePathDefaults.Create(Path, System.IO.Path.Combine(Path, "test.db"), System.IO.Path.Combine(Path, "secrets"))),
                new WebViewHostChannel(), Dispatcher.CurrentDispatcher,
                new PluginPanelResourceReader(NullLogger<PluginPanelResourceReader>.Instance), NullLogger<PluginPanelHostController>.Instance);
        }
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "SelfClawTests", Guid.NewGuid().ToString("N"));
        public ControlledCatalog Catalog { get; }
        public PluginVersionLeaseManager Leases { get; } = new();
        public PluginPanelHostController Host { get; }
        public async Task<JsonElement> OpenAsync(string panel)
        {
            using var document = JsonDocument.Parse($$"""{"panelKey":"plugin/{{panel}}"}""");
            return JsonSerializer.SerializeToElement(await Host.TryHandleAsync("plugin-host/open", document.RootElement));
        }
        public async Task CloseTabAsync(string panel)
        {
            using var document = JsonDocument.Parse($$"""{"panelKey":"plugin/{{panel}}"}""");
            await Host.TryHandleAsync("plugin-host/close", document.RootElement);
        }
        public void Dispose() { Host.Dispose(); Directory.Delete(Path, true); }
    }

    private sealed class ControlledCatalog(string path) : IPluginPanelCatalog, IExtensionPackageRepository
    {
        public TaskCompletionSource Requested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Reads { get; private set; }
        public Task<IReadOnlyList<PluginPanelView>> ListPluginPanelViewsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<PluginPanelView>>([Panel("one"), Panel("two")]);
        public async Task<ExtensionPackageRecord?> GetPackageAsync(ExtensionKind kind, string id, CancellationToken cancellationToken = default)
        {
            Reads++;
            Requested.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            var now = DateTimeOffset.UtcNow;
            return new(ExtensionKind.Plugin, "plugin", "Plugin", "1", "", path, "hash", "{}", null, true, "[]", now, now, now);
        }
        private static PluginPanelView Panel(string id) => new($"plugin/{id}", "plugin", id, id, "", "https://plugin.plugin.selfclaw.local",
            $"https://plugin.plugin.selfclaw.local/{id}.html", 400, true, ExtensionStatus.Ready, ["ui.panel"], []);
        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<ExtensionPackageRecord>> ListPackagesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ExtensionPackageRecord> UpsertPackageAsync(ExtensionPackageRecord package, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetPackageEnabledAsync(ExtensionKind kind, string id, bool enabled, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeletePackageAsync(ExtensionKind kind, string id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
