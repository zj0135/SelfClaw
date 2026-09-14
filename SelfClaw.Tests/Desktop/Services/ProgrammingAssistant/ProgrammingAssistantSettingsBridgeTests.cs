using SelfClaw.Desktop.Services.Settings;
using System.Text.Json;
using FluentAssertions;
using SelfClaw.Desktop.Services.ProgrammingAssistant;
using SelfClaw.Infrastructure.Options;

namespace SelfClaw.Tests.Desktop.Services.ProgrammingAssistant;

public sealed class ProgrammingAssistantSettingsBridgeTests
{
    [Fact]
    public async Task TryHandleAsync_returns_correlated_settings_response()
    {
        var root = Path.Combine(Path.GetTempPath(), "SelfClawBridgeTests", Guid.NewGuid().ToString("N"));
        var paths = StoragePathDefaults.Create(root, Path.Combine(root, "selfclaw.db"), Path.Combine(root, "secrets"));
        var bridge = new ProgrammingAssistantSettingsBridge(
            SelfClaw.Tests.TestDoubles.ProgrammingSettingsTestFactory.Create(new DesktopSettingsJsonStore(paths)),
            new SelfClaw.Desktop.Services.WebView.WebViewHostChannel(), System.Windows.Threading.Dispatcher.CurrentDispatcher);
        using var request = JsonDocument.Parse("""
            { "type": "get-programming-assistant-settings", "requestId": "request-1" }
            """);

        var response = await bridge.TryHandleAsync(
            "get-programming-assistant-settings",
            request.RootElement);

        response.Should().NotBeNull();
        var json = JsonSerializer.Serialize(response);
        using var result = JsonDocument.Parse(json);
        result.RootElement.GetProperty("requestId").GetString().Should().Be("request-1");
        result.RootElement.GetProperty("type").GetString().Should().Be("programming-assistant-settings");
    }

    [Fact]
    public async Task TryHandleAsync_ignores_unrelated_messages()
    {
        var root = Path.Combine(Path.GetTempPath(), "SelfClawBridgeTests");
        var paths = StoragePathDefaults.Create(root, Path.Combine(root, "selfclaw.db"), Path.Combine(root, "secrets"));
        var bridge = new ProgrammingAssistantSettingsBridge(
            SelfClaw.Tests.TestDoubles.ProgrammingSettingsTestFactory.Create(new DesktopSettingsJsonStore(paths)),
            new SelfClaw.Desktop.Services.WebView.WebViewHostChannel(), System.Windows.Threading.Dispatcher.CurrentDispatcher);
        using var request = JsonDocument.Parse("{}");

        var response = await bridge.TryHandleAsync("new-chat", request.RootElement);

        response.Should().BeNull();
    }
}
