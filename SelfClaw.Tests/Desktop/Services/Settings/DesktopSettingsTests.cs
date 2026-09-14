using SelfClaw.Desktop.Services.Settings;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using SelfClaw.Core.Runtime.Agent;
using SelfClaw.Desktop.Services.Appearance;
using SelfClaw.Desktop.Services.Appearance.Models;
using SelfClaw.Desktop.Services.ProgrammingAssistant;
using SelfClaw.Desktop.Services.ProgrammingAssistant.Models;
using SelfClaw.Infrastructure.Options;

namespace SelfClaw.Tests.Desktop.Services.Settings;

public sealed class DesktopSettingsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "SelfClawTests", Guid.NewGuid().ToString("N"));
    private DesktopSettingsJsonStore CreateStore() => new(StoragePathDefaults.Create(_root,
        Path.Combine(_root, "test.db"), Path.Combine(_root, "secrets")));
    private string SettingsPath => Path.Combine(_root, "desktop-settings.json");

    [Fact]
    public async Task Cancellation_between_node_serialization_and_commit_preserves_every_node()
    {
        var store = CreateStore();
        await store.WriteNodeAsync("appearance", "light");
        await store.WriteNodeAsync("pet", new { enabled = true });
        var before = await File.ReadAllBytesAsync(SettingsPath);
        using var cancellation = new CancellationTokenSource();
        var options = new JsonSerializerOptions { Converters = { new CancelingStringConverter(cancellation) } };
        await FluentActions.Awaiting(() => store.WriteNodeAsync("appearance", "dark", options, cancellation.Token))
            .Should().ThrowAsync<OperationCanceledException>();
        (await File.ReadAllBytesAsync(SettingsPath)).Should().Equal(before);
        await store.WriteNodeAsync("programming_assistant", "codex");
        using var result = JsonDocument.Parse(await File.ReadAllTextAsync(SettingsPath));
        result.RootElement.EnumerateObject().Should().HaveCount(3);
        result.RootElement.GetProperty("appearance").GetString().Should().Be("light");
        Directory.EnumerateFiles(_root, "*.tmp").Should().BeEmpty();
    }

    [Fact]
    public async Task Failed_replace_keeps_disk_and_appearance_cache_at_the_last_commit()
    {
        var service = new AppearanceSettingsService(CreateStore());
        var original = await service.SaveAsync(new AppearanceSettings(Mode: "light"));
        using (var locked = new FileStream(SettingsPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            await FluentActions.Awaiting(() => service.SaveAsync(new AppearanceSettings(Mode: "dark")))
                .Should().ThrowAsync<IOException>();
            (await service.GetAsync()).Should().Be(original);
        }
        (await new AppearanceSettingsService(CreateStore()).GetAsync()).Should().Be(original);
    }

    [Theory]
    [InlineData("{broken")]
    [InlineData("[]")]
    public async Task Unreadable_configuration_is_reported_and_never_replaced_with_an_empty_object(string json)
    {
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(SettingsPath, json);
        var store = CreateStore();
        await FluentActions.Awaiting(() => store.ReadNodeAsync<string>("pet")).Should().ThrowAsync<JsonException>();
        await FluentActions.Awaiting(() => store.WriteNodeAsync("pet", true)).Should().ThrowAsync<JsonException>();
        (await File.ReadAllTextAsync(SettingsPath)).Should().Be(json);
    }

    [Fact]
    public async Task Concurrent_feature_saves_preserve_all_nodes()
    {
        var store = CreateStore();
        await Task.WhenAll(Enumerable.Range(0, 24).Select(index => store.WriteNodeAsync($"feature-{index}", index)));
        using var result = JsonDocument.Parse(await File.ReadAllTextAsync(SettingsPath));
        result.RootElement.EnumerateObject().Should().HaveCount(24);
        for (var i = 0; i < 24; i++) result.RootElement.GetProperty($"feature-{i}").GetInt32().Should().Be(i);
    }

    [Fact]
    public async Task Failed_cli_save_preserves_confirmed_invocation()
    {
        var store = CreateStore();
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new JsonStringEnumConverter() } };
        await store.WriteNodeAsync("programming_assistant", new ProgrammingAssistantSettings
        {
            HasScanned = true, SelectedCliId = "codex", SelectedModel = "model-a",
            Tools = [new DetectedProgrammingCli("codex", CliAgentKind.Codex, "Codex", "OpenAI", "1.0", ["model-a", "model-b"], ["high"])]
        }, options);
        var service = SelfClaw.Tests.TestDoubles.ProgrammingSettingsTestFactory.Create(store);
        await service.GetCurrentAsync();
        using (var locked = new FileStream(SettingsPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            await FluentActions.Awaiting(() => service.SelectModelAsync("model-b")).Should().ThrowAsync<IOException>();
        (await service.GetSelectedInvocationAsync())?.Model.Should().Be("model-a");
        (await SelfClaw.Tests.TestDoubles.ProgrammingSettingsTestFactory.Create(CreateStore()).GetSelectedInvocationAsync())?.Model.Should().Be("model-a");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class CancelingStringConverter(CancellationTokenSource cancellation) : JsonConverter<string>
    {
        public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => reader.GetString() ?? string.Empty;
        public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
        {
            writer.WriteStringValue(value);
            cancellation.Cancel();
        }
    }
}
