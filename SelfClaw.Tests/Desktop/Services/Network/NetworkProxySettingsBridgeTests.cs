using System.Text.Json;
using System.Windows.Threading;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using SelfClaw.Core.Models;
using SelfClaw.Desktop.Services.Network;
using SelfClaw.Desktop.Services.Settings;
using SelfClaw.Desktop.Services.WebView;
using SelfClaw.Infrastructure.Options;
using SelfClaw.Tests.TestDoubles;

namespace SelfClaw.Tests.Desktop.Services.Network;

public sealed class NetworkProxySettingsBridgeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "SelfClawTests", Guid.NewGuid().ToString("N"));
    private readonly List<string> _posted = [];

    private string SettingsPath => Path.Combine(_root, "desktop-settings.json");

    [Theory]
    [InlineData("network-proxy/get", "{}")]
    [InlineData("network-proxy/get", "{\"requestId\":\"req-7\"}")]
    [InlineData("network-proxy/save", "{\"requestId\":\"req-7\",\"mode\":\"Custom\",\"host\":\"127.0.0.1\",\"port\":7890}")]
    public async Task TryHandleAsync_echoes_the_request_id_and_answers_with_the_state_type(string type, string payloadJson)
    {
        var bridge = CreateBridge(out _);
        using var document = JsonDocument.Parse(payloadJson);

        var response = await bridge.TryHandleAsync(type, document.RootElement);

        response.Should().NotBeNull();
        var responseJson = Serialize(response!.Value.Response);
        responseJson.GetProperty("type").GetString().Should().Be("network-proxy/state");
        var requestId = Parse(payloadJson).TryGetProperty("requestId", out var echoed) ? echoed.GetString() : null;
        if (requestId is null)
        {
            responseJson.TryGetProperty("requestId", out _).Should().BeFalse();
        }
        else
        {
            responseJson.GetProperty("requestId").GetString().Should().Be(requestId);
        }
    }

    [Fact]
    public async Task Saving_persists_the_exit_and_echoes_it()
    {
        var bridge = CreateBridge(out var service);
        using var document = JsonDocument.Parse(
            """{ "requestId": "save-1", "mode": "Custom", "host": "127.0.0.1", "port": 7890 }""");

        var response = await bridge.TryHandleAsync("network-proxy/save", document.RootElement);

        var responseJson = Serialize(response!.Value.Response);
        responseJson.TryGetProperty("error", out _).Should().BeFalse();
        responseJson.GetProperty("mode").GetString().Should().Be("Custom");
        responseJson.GetProperty("host").GetString().Should().Be("127.0.0.1");
        responseJson.GetProperty("port").GetInt32().Should().Be(7890);
        (await service.GetAsync()).Should().Be(new NetworkProxySettings(NetworkProxyMode.Custom, "127.0.0.1", 7890));
        (await File.ReadAllTextAsync(SettingsPath)).Should().Contain("\"network_proxy\"");
    }

    [Fact]
    public async Task An_unparsable_mode_is_normalized_to_the_default_exit_instead_of_failing()
    {
        var bridge = CreateBridge(out _);
        using var document = JsonDocument.Parse("""{ "mode": "NoSuchMode", "host": "127.0.0.1", "port": 7890 }""");

        var response = await bridge.TryHandleAsync("network-proxy/save", document.RootElement);

        var responseJson = Serialize(response!.Value.Response);
        responseJson.TryGetProperty("error", out _).Should().BeFalse();
        responseJson.GetProperty("mode").GetString().Should().Be("Direct");
    }

    [Fact]
    public async Task TryHandleAsync_returns_null_for_unrecognized_messages()
    {
        var bridge = CreateBridge(out _);

        (await bridge.TryHandleAsync("network-proxy/unknown", Parse("{}"))).Should().BeNull();
        (await bridge.TryHandleAsync("workspace-tools/get", Parse("{}"))).Should().BeNull();
    }

    [Fact]
    public Task Saving_broadcasts_one_state_push_without_a_request_id()
        => WpfDispatcherTest.RunAsync(async () =>
        {
            var bridge = CreateBridge(out _);
            using var document = JsonDocument.Parse("""{ "requestId": "save-2", "mode": "System" }""");

            await bridge.TryHandleAsync("network-proxy/save", document.RootElement);
            // 推送经 InvokeAsync 排在 Dispatcher 队列上，让队列先跑完再断言。
            await Dispatcher.Yield(DispatcherPriority.Background);

            _posted.Should().ContainSingle();
            var push = JsonDocument.Parse(_posted[0]).RootElement;
            push.GetProperty("type").GetString().Should().Be("network-proxy/state");
            push.TryGetProperty("requestId", out _).Should().BeFalse();
            push.GetProperty("mode").GetString().Should().Be("System");
        });

    [Fact]
    public Task Reading_does_not_broadcast()
        => WpfDispatcherTest.RunAsync(async () =>
        {
            var bridge = CreateBridge(out _);

            await bridge.TryHandleAsync("network-proxy/get", Parse("{\"requestId\":\"get-1\"}"));
            await Dispatcher.Yield(DispatcherPriority.Background);

            _posted.Should().BeEmpty();
        });

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private NetworkProxySettingsBridge CreateBridge(out NetworkProxySettingsService service)
    {
        var channel = new WebViewHostChannel();
        channel.Attach(json => _posted.Add(json));
        channel.MarkReady();
        service = new NetworkProxySettingsService(
            new DesktopSettingsJsonStore(StoragePathDefaults.Create(_root,
                Path.Combine(_root, "test.db"), Path.Combine(_root, "secrets"))));
        return new NetworkProxySettingsBridge(
            service,
            channel,
            Dispatcher.CurrentDispatcher,
            NullLogger<NetworkProxySettingsBridge>.Instance);
    }

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static JsonElement Serialize(object response)
        => JsonDocument.Parse(JsonSerializer.Serialize(response, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        })).RootElement;
}
