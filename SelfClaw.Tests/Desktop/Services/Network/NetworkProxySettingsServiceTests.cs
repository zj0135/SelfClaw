using FluentAssertions;
using SelfClaw.Core.Models;
using SelfClaw.Desktop.Services.Network;
using SelfClaw.Desktop.Services.Settings;
using SelfClaw.Infrastructure.Options;

namespace SelfClaw.Tests.Desktop.Services.Network;

public sealed class NetworkProxySettingsServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "SelfClawTests", Guid.NewGuid().ToString("N"));

    private string SettingsPath => Path.Combine(_root, "desktop-settings.json");

    [Fact]
    public async Task Missing_node_connects_directly()
    {
        var service = CreateService();

        var settings = await service.GetAsync();

        settings.Mode.Should().Be(NetworkProxyMode.Direct);
        settings.Should().Be(new NetworkProxySettings());
        service.Current.Should().Be(settings);
    }

    [Fact]
    public void Current_answers_the_default_before_the_first_load()
    {
        var service = CreateService();

        // A request racing the startup warm-up must use the same exit a missing node resolves to.
        service.Current.Should().Be(new NetworkProxySettings());
    }

    [Fact]
    public async Task Saving_persists_the_exit_and_a_fresh_service_reads_it_back()
    {
        var service = CreateService();

        await service.SaveAsync(new NetworkProxySettings(NetworkProxyMode.Custom, "127.0.0.1", 7890));

        (await File.ReadAllTextAsync(SettingsPath)).Should().Contain("\"network_proxy\"");
        (await service.GetAsync()).Should().Be(new NetworkProxySettings(NetworkProxyMode.Custom, "127.0.0.1", 7890));
        (await CreateService().GetAsync()).Should().Be(new NetworkProxySettings(NetworkProxyMode.Custom, "127.0.0.1", 7890));
    }

    [Fact]
    public async Task Saving_the_same_settings_neither_writes_nor_notifies()
    {
        var service = CreateService();
        await service.SaveAsync(new NetworkProxySettings(NetworkProxyMode.System));
        var notified = 0;
        service.Changed += _ => notified++;
        File.Delete(SettingsPath);

        var saved = await service.SaveAsync(new NetworkProxySettings(NetworkProxyMode.System));

        saved.Mode.Should().Be(NetworkProxyMode.System);
        File.Exists(SettingsPath).Should().BeFalse();
        notified.Should().Be(0);
    }

    [Fact]
    public async Task Saving_different_settings_notifies_with_the_new_value()
    {
        var service = CreateService();
        NetworkProxySettings? observed = null;
        service.Changed += settings => observed = settings;

        await service.SaveAsync(new NetworkProxySettings(NetworkProxyMode.Custom, "127.0.0.1", 7890));

        observed.Should().Be(new NetworkProxySettings(NetworkProxyMode.Custom, "127.0.0.1", 7890));
    }

    [Theory]
    [InlineData("""{ "network_proxy": { "mode": 47 } }""")]
    [InlineData("""{ "network_proxy": { "mode": "NoSuchMode" } }""")]
    [InlineData("""{ "network_proxy": { "host": "127.0.0.1", "port": 7890 } }""")]
    public async Task An_unusable_mode_falls_back_to_the_default_exit(string json)
    {
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(SettingsPath, json);

        (await CreateService().GetAsync()).Mode.Should().Be(NetworkProxyMode.Direct);
    }

    [Fact]
    public async Task An_unusable_port_is_kept_as_zero_and_the_host_is_trimmed()
    {
        var service = CreateService();

        var saved = await service.SaveAsync(new NetworkProxySettings(NetworkProxyMode.Custom, "  127.0.0.1  ", 70_000));

        // The mode survives so the settings page does not flip under the user while the port is being typed.
        saved.Should().Be(new NetworkProxySettings(NetworkProxyMode.Custom, "127.0.0.1", 0));
    }

    [Theory]
    [InlineData("http://127.0.0.1", "127.0.0.1")]
    [InlineData("https://127.0.0.1/", "127.0.0.1")]
    [InlineData("  http://proxy.test:8080/  ", "proxy.test:8080")]
    public async Task Protocol_prefixes_and_trailing_slashes_are_stripped_from_host(string host, string expectedHost)
    {
        var service = CreateService();

        var saved = await service.SaveAsync(new NetworkProxySettings(NetworkProxyMode.Custom, host, 7890));

        saved.Host.Should().Be(expectedHost);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private NetworkProxySettingsService CreateService()
        => new(new DesktopSettingsJsonStore(StoragePathDefaults.Create(_root,
            Path.Combine(_root, "test.db"), Path.Combine(_root, "secrets"))));
}
