using System.Text.Json;
using System.Windows.Threading;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using SelfClaw.Core.Models;
using SelfClaw.Desktop.Services.Settings;
using SelfClaw.Desktop.Services.Tools;
using SelfClaw.Desktop.Services.WebView;
using SelfClaw.Infrastructure.Options;
using SelfClaw.Tests.TestDoubles;

namespace SelfClaw.Tests.Desktop.Services.Tools;

public sealed class WorkspaceToolsSettingsBridgeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "SelfClawTests", Guid.NewGuid().ToString("N"));
    private readonly List<string> _posted = [];

    private string SettingsPath => Path.Combine(_root, "desktop-settings.json");

    [Theory]
    [InlineData("workspace-tools/get", "{}")]
    [InlineData("workspace-tools/get", "{\"requestId\":\"req-7\"}")]
    [InlineData("workspace-tools/save", "{\"requestId\":\"req-7\",\"shellKind\":\"CommandPrompt\"}")]
    public async Task TryHandleAsync_echoes_the_request_id_and_answers_with_the_state_type(string type, string payloadJson)
    {
        var bridge = CreateBridge(out _);
        using var document = JsonDocument.Parse(payloadJson);

        var response = await bridge.TryHandleAsync(type, document.RootElement);

        response.Should().NotBeNull();
        var responseJson = Serialize(response!.Value.Response);
        responseJson.GetProperty("type").GetString().Should().Be("workspace-tools/state");
        responseJson.GetProperty("shells").GetArrayLength().Should().Be(4);
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
    public async Task Saving_persists_the_kind_and_echoes_it()
    {
        var bridge = CreateBridge(out var service);
        using var document = JsonDocument.Parse("""{ "requestId": "save-1", "shellKind": "PowerShellSeven" }""");

        var response = await bridge.TryHandleAsync("workspace-tools/save", document.RootElement);

        var responseJson = Serialize(response!.Value.Response);
        responseJson.TryGetProperty("error", out _).Should().BeFalse();
        responseJson.GetProperty("shellKind").GetString().Should().Be("PowerShellSeven");
        (await service.GetPreferenceAsync()).ShellKind.Should().Be(WorkspaceShellKind.PowerShellSeven);
        (await File.ReadAllTextAsync(SettingsPath)).Should().Contain("\"workspace_tools\"");
    }

    [Fact]
    public async Task An_unparsable_kind_is_normalized_to_the_default_instead_of_failing()
    {
        var bridge = CreateBridge(out _);
        using var document = JsonDocument.Parse("""{ "shellKind": "NoSuchShell" }""");

        var response = await bridge.TryHandleAsync("workspace-tools/save", document.RootElement);

        var responseJson = Serialize(response!.Value.Response);
        responseJson.TryGetProperty("error", out _).Should().BeFalse();
        responseJson.GetProperty("shellKind").GetString().Should().Be("WindowsPowerShell");
    }

    [Fact]
    public async Task TryHandleAsync_returns_null_for_unrecognized_messages()
    {
        var bridge = CreateBridge(out _);

        (await bridge.TryHandleAsync("workspace-tools/unknown", Parse("{}"))).Should().BeNull();
        (await bridge.TryHandleAsync("direct-compression/get", Parse("{}"))).Should().BeNull();
    }

    [Fact]
    public Task Saving_broadcasts_one_state_push_without_a_request_id()
        => WpfDispatcherTest.RunAsync(async () =>
        {
            var bridge = CreateBridge(out _);
            using var document = JsonDocument.Parse("""{ "requestId": "save-2", "shellKind": "CommandPrompt" }""");

            await bridge.TryHandleAsync("workspace-tools/save", document.RootElement);
            // 推送经 InvokeAsync 排在 Dispatcher 队列上，让队列先跑完再断言。
            await Dispatcher.Yield(DispatcherPriority.Background);

            _posted.Should().ContainSingle();
            var push = JsonDocument.Parse(_posted[0]).RootElement;
            push.GetProperty("type").GetString().Should().Be("workspace-tools/state");
            push.TryGetProperty("requestId", out _).Should().BeFalse();
            push.GetProperty("shellKind").GetString().Should().Be("CommandPrompt");
        });

    [Fact]
    public Task Reading_does_not_broadcast()
        => WpfDispatcherTest.RunAsync(async () =>
        {
            var bridge = CreateBridge(out _);

            await bridge.TryHandleAsync("workspace-tools/get", Parse("{\"requestId\":\"get-1\"}"));
            await Dispatcher.Yield(DispatcherPriority.Background);

            _posted.Should().BeEmpty();
        });

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private WorkspaceToolsSettingsBridge CreateBridge(out WorkspaceToolsSettingsService service)
    {
        var channel = new WebViewHostChannel();
        channel.Attach(json => _posted.Add(json));
        channel.MarkReady();
        service = new WorkspaceToolsSettingsService(
            new DesktopSettingsJsonStore(StoragePathDefaults.Create(_root,
                Path.Combine(_root, "test.db"), Path.Combine(_root, "secrets"))));
        return new WorkspaceToolsSettingsBridge(
            service,
            channel,
            Dispatcher.CurrentDispatcher,
            NullLogger<WorkspaceToolsSettingsBridge>.Instance);
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
