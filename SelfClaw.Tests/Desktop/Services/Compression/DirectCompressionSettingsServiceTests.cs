using FluentAssertions;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Desktop.Services.Compression;
using SelfClaw.Desktop.Services.Settings;
using SelfClaw.Infrastructure.Options;
using SelfClaw.Tests.TestDoubles;

namespace SelfClaw.Tests.Desktop.Services.Compression;

public sealed class DirectCompressionSettingsServiceTests : IDisposable
{
    private static readonly DirectCompressionPolicy DefaultPolicy = new();

    private readonly string _root = Path.Combine(Path.GetTempPath(), "SelfClawTests", Guid.NewGuid().ToString("N"));
    private readonly Guid _availableModelId = Guid.NewGuid();

    private string SettingsPath => Path.Combine(_root, "desktop-settings.json");

    [Theory]
    [InlineData(0.0d, 0.10d)]
    [InlineData(0.09d, 0.10d)]
    [InlineData(0.5d, 0.5d)]
    [InlineData(0.99d, 0.95d)]
    [InlineData(-3d, 0.10d)]
    [InlineData(double.NaN, 0.75d)]
    [InlineData(double.PositiveInfinity, 0.75d)]
    [InlineData(double.NegativeInfinity, 0.75d)]
    public async Task Threshold_ratio_is_clamped_and_non_finite_values_fall_back_to_the_default(double stored, double expected)
    {
        var service = CreateService();
        await service.SaveAsync(new DirectCompressionPolicy(ThresholdRatio: stored));

        (await service.GetAsync()).ThresholdRatio.Should().BeApproximately(expected, 1e-9);
    }

    [Theory]
    [InlineData(0, 4_000)]
    [InlineData(-1, 4_000)]
    [InlineData(4_000, 4_000)]
    [InlineData(64_000, 64_000)]
    [InlineData(1_000_001, 1_000_000)]
    [InlineData(int.MaxValue, 1_000_000)]
    public async Task Absolute_threshold_is_clamped(int stored, int expected)
    {
        var service = CreateService();
        await service.SaveAsync(new DirectCompressionPolicy(AbsoluteThresholdTokens: stored));

        (await service.GetAsync()).AbsoluteThresholdTokens.Should().Be(expected);
    }

    [Fact]
    public async Task Unknown_model_is_reset_to_follow_the_turn_model()
    {
        var service = CreateService();
        await service.SaveAsync(new DirectCompressionPolicy(ModelProfileId: Guid.NewGuid()));

        (await service.GetAsync()).ModelProfileId.Should().BeNull();
    }

    [Fact]
    public async Task Available_model_is_kept()
    {
        var service = CreateService();
        var saved = await service.SaveAsync(new DirectCompressionPolicy(ModelProfileId: _availableModelId));

        saved.ModelProfileId.Should().Be(_availableModelId);
        (await service.GetAsync()).ModelProfileId.Should().Be(_availableModelId);
        (await CreateService().GetAsync()).ModelProfileId.Should().Be(_availableModelId);
    }

    [Fact]
    public async Task Save_writes_disk_updates_cache_and_raises_changed_once()
    {
        var service = CreateService();
        var changes = 0;
        service.Changed += () => changes++;
        var policy = new DirectCompressionPolicy(
            Enabled: false,
            ThresholdRatio: 0.5d,
            AbsoluteThresholdTokens: 12_000,
            ModelProfileId: _availableModelId);

        var saved = await service.SaveAsync(policy);

        changes.Should().Be(1);
        saved.Should().Be(new DirectCompressionPolicy(false, 0.5d, 12_000, _availableModelId));
        (await CreateService().GetAsync()).Should().Be(saved);
        (await File.ReadAllTextAsync(SettingsPath)).Should().Contain("\"direct_compression\"");
        // 缓存在 Save 后立刻生效：即使磁盘已不可读，读到的仍是刚保存的值。
        File.Delete(SettingsPath);
        (await service.GetAsync()).Should().Be(saved);
    }

    [Fact]
    public async Task Changed_is_not_raised_by_loading()
    {
        await CreateService().SaveAsync(new DirectCompressionPolicy());
        var service = CreateService();
        var changes = 0;
        service.Changed += () => changes++;

        await service.GetAsync();

        changes.Should().Be(0);
    }

    [Fact]
    public async Task Missing_settings_file_falls_back_to_the_default_policy()
    {
        (await CreateService().GetAsync()).Should().Be(DefaultPolicy);
    }

    [Theory]
    [InlineData("{broken")]
    [InlineData("[]")]
    [InlineData("""{ "direct_compression": { "thresholdRatio": "wide" } }""")]
    public async Task Corrupt_settings_file_falls_back_to_the_default_policy(string json)
    {
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(SettingsPath, json);

        (await CreateService().GetAsync()).Should().Be(DefaultPolicy);
    }

    [Fact]
    public async Task Stored_node_normalizes_on_load()
    {
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(SettingsPath,
            """
            { "direct_compression": { "enabled": true, "thresholdRatio": 2.5, "absoluteThresholdTokens": 10 } }
            """);

        var policy = await CreateService().GetAsync();

        policy.Enabled.Should().BeTrue();
        policy.ThresholdRatio.Should().Be(0.95d);
        policy.AbsoluteThresholdTokens.Should().Be(4_000);
        policy.ModelProfileId.Should().BeNull();
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private DesktopSettingsJsonStore CreateStore() => new(StoragePathDefaults.Create(_root,
        Path.Combine(_root, "test.db"), Path.Combine(_root, "secrets")));

    private IAiModelCatalog CreateCatalog() => new StubAiModelCatalog
    {
        EnabledModels = [new EnabledModelView(_availableModelId, "压缩模型", "compact-model", "测试提供商")],
    };

    private DirectCompressionSettingsService CreateService() => new(CreateStore(), CreateCatalog());
}
