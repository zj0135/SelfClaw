using System.Text.Json;
using FluentAssertions;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Desktop.Services.Usage;

namespace SelfClaw.Tests.Desktop.Services.Usage;

public sealed class UsageStatisticsBridgeTests
{
    private static readonly DateOnly Today = new(2026, 8, 31);

    [Fact]
    public async Task TryHandleAsync_answers_the_overview_wire_contract()
    {
        var reader = new RecordingUsageOverviewReader(Overview(UsageRange.ThirtyDays));
        using var document = JsonDocument.Parse("""{ "requestId": "host-1", "range": "30d", "timeZone": "UTC" }""");

        var response = Serialize(await new UsageStatisticsBridge(reader).TryHandleAsync(
            "usage/get-overview", document.RootElement));

        response.GetProperty("type").GetString().Should().Be("usage-overview");
        response.GetProperty("requestId").GetString().Should().Be("host-1");
        response.GetProperty("range").GetString().Should().Be("30d");
        response.GetProperty("timeZone").GetString().Should().Be("UTC");
        response.GetProperty("generatedAt").GetInt64().Should().Be(1_730_000_000_000);
        reader.Queries.Should().ContainSingle().Which.Range.Should().Be(UsageRange.ThirtyDays);

        var app = response.GetProperty("app");
        app.GetProperty("version").GetString().Should().NotBeNullOrWhiteSpace();
        app.GetProperty("runtime").GetString().Should().NotBeNullOrWhiteSpace();
        app.GetProperty("os").GetString().Should().NotBeNullOrWhiteSpace();

        var lifetime = response.GetProperty("lifetime");
        lifetime.GetProperty("summary").GetProperty("totalTokens").GetInt64().Should().Be(1_240_000);
        lifetime.GetProperty("heatmap").GetProperty("days")[0].GetProperty("date").GetString()
            .Should().Be("2026-08-31");
        var rangeData = response.GetProperty("rangeData");
        rangeData.GetProperty("daily")[0].GetProperty("date").GetString().Should().Be("2026-08-31");
        rangeData.GetProperty("models")[0].GetProperty("share").GetDouble().Should().BeApproximately(0.5, 0.0001);
        rangeData.GetProperty("tools")[0].GetProperty("toolName").GetString().Should().Be("search_text");
        // 范围概要没有渲染方：数据面只下发真正被画出来的两段，不留「将来会用」的字段。
        rangeData.TryGetProperty("summary", out _).Should().BeFalse();
    }

    [Fact]
    public async Task TryHandleAsync_defaults_a_missing_range_to_seven_days()
    {
        var reader = new RecordingUsageOverviewReader(Overview(UsageRange.SevenDays));
        using var document = JsonDocument.Parse("""{ "requestId": "host-2" }""");

        var response = Serialize(await new UsageStatisticsBridge(reader).TryHandleAsync(
            "usage/get-overview", document.RootElement));

        response.GetProperty("range").GetString().Should().Be("7d");
        reader.Queries.Should().ContainSingle().Which.Range.Should().Be(UsageRange.SevenDays);
        reader.Queries[0].TimeZone.Should().Be(TimeZoneInfo.Local);
    }

    [Fact]
    public async Task TryHandleAsync_rejects_an_unknown_range()
    {
        var reader = new RecordingUsageOverviewReader(Overview(UsageRange.SevenDays));
        using var document = JsonDocument.Parse("""{ "requestId": "host-3", "range": "90d" }""");
        var bridge = new UsageStatisticsBridge(reader);

        var act = () => bridge.TryHandleAsync("usage/get-overview", document.RootElement);

        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*90d*");
        reader.Queries.Should().BeEmpty();
    }

    [Fact]
    public async Task TryHandleAsync_falls_back_to_the_local_zone_for_an_unknown_time_zone()
    {
        var reader = new RecordingUsageOverviewReader(Overview(UsageRange.SevenDays));
        using var document = JsonDocument.Parse("""{ "requestId": "host-4", "timeZone": "Not/AZone" }""");

        await new UsageStatisticsBridge(reader).TryHandleAsync("usage/get-overview", document.RootElement);

        reader.Queries.Should().ContainSingle().Which.TimeZone.Should().Be(TimeZoneInfo.Local);
    }

    [Fact]
    public async Task TryHandleAsync_returns_null_for_messages_it_does_not_own()
    {
        var reader = new RecordingUsageOverviewReader(Overview(UsageRange.SevenDays));
        var bridge = new UsageStatisticsBridge(reader);
        using var document = JsonDocument.Parse("{}");

        (await bridge.TryHandleAsync("usage/unknown", document.RootElement)).Should().BeNull();
        (await bridge.TryHandleAsync("appearance/get-state", document.RootElement)).Should().BeNull();
        reader.Queries.Should().BeEmpty();
    }

    [Fact]
    public async Task TryHandleAsync_propagates_cancellation_instead_of_reporting_a_failure()
    {
        var reader = new RecordingUsageOverviewReader(Overview(UsageRange.SevenDays))
        {
            Failure = new OperationCanceledException("host is closing")
        };
        using var document = JsonDocument.Parse("""{ "requestId": "host-5" }""");
        var bridge = new UsageStatisticsBridge(reader);

        var act = () => bridge.TryHandleAsync("usage/get-overview", document.RootElement);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    private static UsageOverview Overview(UsageRange range)
    {
        var summary = new UsageSummary(
            TotalTokens: 1_240_000,
            InputTokens: 980_000,
            OutputTokens: 260_000,
            ReasoningTokens: 42_000,
            CachedInputTokens: 510_000,
            CacheWriteInputTokens: 32_000,
            CacheHitRate: 0.52,
            TotalTurns: 86,
            TotalSessions: 14,
            ActiveDays: 12,
            CurrentStreakDays: 3,
            LongestStreakDays: 7,
            LongestTurnMs: 412_000,
            PeakDayTokens: 86_400,
            PeakDayDate: Today,
            FavoriteModel: new UsageFavoriteModel("glm-4.6", 620_000, 0.5),
            ToolCallCount: 128,
            ToolErrorRate: 0.023,
            FirstActivityDate: Today.AddDays(-81),
            LastActivityDate: Today);

        return new UsageOverview(
            DateTimeOffset.FromUnixTimeMilliseconds(1_730_000_000_000),
            "UTC",
            range,
            new UsageLifetimeSnapshot(
                summary,
                new UsageHeatmap(Today, Today, 86_400, [new UsageHeatmapDay(Today, 86_400, 4, 9)])),
            [new UsageDailyPoint(Today, 86_400, 70_000, 16_400, 4, [new UsageDailyModelCount("glm-4.6", 86_400)])],
            [new UsageModelUsage("glm-4.6", 620_000, 500_000, 120_000, 42, 0.5)],
            [new UsageToolUsage("search_text", "搜索文本", 128, 3, 0.023, 420)]);
    }

    private static JsonElement Serialize(object? response)
        => JsonDocument.Parse(JsonSerializer.Serialize(response, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        })).RootElement;

    private sealed class RecordingUsageOverviewReader(UsageOverview overview) : IUsageOverviewReader
    {
        public Exception? Failure { get; init; }

        public List<UsageOverviewQuery> Queries { get; } = [];

        public Task<UsageOverview> ReadAsync(UsageOverviewQuery query, CancellationToken cancellationToken = default)
        {
            if (Failure is not null)
            {
                throw Failure;
            }

            Queries.Add(query);
            return Task.FromResult(overview);
        }
    }
}
