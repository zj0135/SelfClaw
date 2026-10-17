using System.Text.Json;
using System.Windows.Threading;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Desktop.Services.Compression;
using SelfClaw.Desktop.Services.Settings;
using SelfClaw.Desktop.Services.WebView;
using SelfClaw.Infrastructure.Options;
using SelfClaw.Tests.TestDoubles;

namespace SelfClaw.Tests.Desktop.Services.Compression;

public sealed class DirectCompressionSettingsBridgeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "SelfClawTests", Guid.NewGuid().ToString("N"));
    private readonly Guid _availableModelId = Guid.NewGuid();
    private readonly List<string> _posted = [];

    private string SettingsPath => Path.Combine(_root, "desktop-settings.json");

    [Theory]
    [InlineData("direct-compression/get", "{}")]
    [InlineData("direct-compression/get", "{\"requestId\":\"req-7\"}")]
    [InlineData("direct-compression/save", "{\"requestId\":\"req-7\",\"policy\":{\"enabled\":false}}")]
    public async Task TryHandleAsync_echoes_the_request_id_and_answers_with_the_state_type(string type, string payloadJson)
    {
        var bridge = CreateBridge(out _);
        using var document = JsonDocument.Parse(payloadJson);

        var response = await bridge.TryHandleAsync(type, document.RootElement);

        response.Should().NotBeNull();
        var responseJson = Serialize(response!.Value.Response);
        responseJson.GetProperty("type").GetString().Should().Be("direct-compression/state");
        responseJson.GetProperty("policy").TryGetProperty("thresholdRatio", out _).Should().BeTrue();
        responseJson.TryGetProperty("error", out _).Should().BeFalse();
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

    [Theory]
    [InlineData(2.5d, 10, 0.95d, 4_000)]
    [InlineData(-1d, 0, 0.10d, 4_000)]
    [InlineData(0.5d, 12_000, 0.5d, 12_000)]
    [InlineData(0.5d, 9_000_000, 0.5d, 1_000_000)]
    public async Task Out_of_range_values_are_normalized_and_persisted_instead_of_failing(
        double ratio,
        int absoluteThresholdTokens,
        double expectedRatio,
        int expectedAbsoluteThresholdTokens)
    {
        var bridge = CreateBridge(out var service);
        using var document = JsonDocument.Parse($$"""
            { "requestId": "save-1", "policy": { "thresholdRatio": {{ratio.ToString(System.Globalization.CultureInfo.InvariantCulture)}}, "absoluteThresholdTokens": {{absoluteThresholdTokens}} } }
            """);

        var response = await bridge.TryHandleAsync("direct-compression/save", document.RootElement);

        var responseJson = Serialize(response!.Value.Response);
        responseJson.TryGetProperty("error", out _).Should().BeFalse();
        var policy = responseJson.GetProperty("policy");
        policy.GetProperty("thresholdRatio").GetDouble().Should().BeApproximately(expectedRatio, 1e-9);
        policy.GetProperty("absoluteThresholdTokens").GetInt32().Should().Be(expectedAbsoluteThresholdTokens);
        (await service.GetAsync()).ThresholdRatio.Should().BeApproximately(expectedRatio, 1e-9);
        (await File.ReadAllTextAsync(SettingsPath)).Should().Contain("\"direct_compression\"");
    }

    [Fact]
    public async Task Unknown_model_is_normalized_to_follow_the_turn_model()
    {
        var bridge = CreateBridge(out _);
        using var document = JsonDocument.Parse($$"""
            { "policy": { "modelProfileId": "{{Guid.NewGuid()}}" } }
            """);

        var response = await bridge.TryHandleAsync("direct-compression/save", document.RootElement);

        var policy = Serialize(response!.Value.Response).GetProperty("policy");
        policy.TryGetProperty("modelProfileId", out _).Should().BeFalse();
    }

    [Fact]
    public async Task TryHandleAsync_returns_null_for_unrecognized_messages()
    {
        var bridge = CreateBridge(out var service);

        (await bridge.TryHandleAsync("direct-compression/unknown", Parse("{}"))).Should().BeNull();
        (await bridge.TryHandleAsync("appearance/get-state", Parse("{}"))).Should().BeNull();

        (await service.GetAsync()).Should().Be(new DirectCompressionPolicy());
    }

    [Fact]
    public Task Saving_broadcasts_one_state_push_without_a_request_id()
        => WpfDispatcherTest.RunAsync(async () =>
        {
            var bridge = CreateBridge(out _);
            using var document = JsonDocument.Parse("""{ "requestId": "save-2", "policy": { "enabled": false } }""");

            await bridge.TryHandleAsync("direct-compression/save", document.RootElement);
            // 推送经 InvokeAsync 排在 Dispatcher 队列上，让队列先跑完再断言。
            await Dispatcher.Yield(DispatcherPriority.Background);

            _posted.Should().ContainSingle();
            var push = JsonDocument.Parse(_posted[0]).RootElement;
            push.GetProperty("type").GetString().Should().Be("direct-compression/state");
            push.TryGetProperty("requestId", out _).Should().BeFalse();
            push.GetProperty("policy").GetProperty("enabled").GetBoolean().Should().BeFalse();
        });

    [Fact]
    public Task Reading_does_not_broadcast()
        => WpfDispatcherTest.RunAsync(async () =>
        {
            var bridge = CreateBridge(out _);

            await bridge.TryHandleAsync("direct-compression/get", Parse("{\"requestId\":\"get-1\"}"));
            await Dispatcher.Yield(DispatcherPriority.Background);

            _posted.Should().BeEmpty();
        });

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private DirectCompressionSettingsBridge CreateBridge(out DirectCompressionSettingsService service)
    {
        var channel = new WebViewHostChannel();
        channel.Attach(json => _posted.Add(json));
        channel.MarkReady();
        service = new DirectCompressionSettingsService(
            new DesktopSettingsJsonStore(StoragePathDefaults.Create(_root,
                Path.Combine(_root, "test.db"), Path.Combine(_root, "secrets"))),
            new StubAiModelCatalog
            {
                EnabledModels = [new EnabledModelView(_availableModelId, "压缩模型", "compact-model", "测试提供商")],
            });
        return new DirectCompressionSettingsBridge(
            service,
            channel,
            Dispatcher.CurrentDispatcher,
            NullLogger<DirectCompressionSettingsBridge>.Instance);
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
