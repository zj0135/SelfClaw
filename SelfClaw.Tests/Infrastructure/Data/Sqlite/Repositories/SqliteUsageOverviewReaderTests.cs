using FluentAssertions;
using SelfClaw.Core.Models;
using SelfClaw.Infrastructure.Data.Sqlite.Repositories;
using SelfClaw.Tests.TestDoubles;

namespace SelfClaw.Tests.Infrastructure.Data.Sqlite.Repositories;

public sealed class SqliteUsageOverviewReaderTests
{
    private const string BlankModel = "";

    [Fact]
    public async Task ReadAsync_returns_zeros_and_filled_heatmap_days_for_an_empty_database()
    {
        using var fixture = new ConversationPersistenceFixture();
        var overview = await new SqliteUsageOverviewReader(fixture.Database).ReadAsync(Query(UsageRange.ThirtyDays));

        overview.Lifetime.Summary.TotalTokens.Should().Be(0);
        overview.Lifetime.Summary.TotalTurns.Should().Be(0);
        overview.Lifetime.Summary.TotalSessions.Should().Be(0);
        overview.Lifetime.Summary.ActiveDays.Should().Be(0);
        overview.Lifetime.Summary.CacheHitRate.Should().BeNull();
        overview.Lifetime.Summary.ToolErrorRate.Should().BeNull();
        overview.Lifetime.Summary.FavoriteModel.Should().BeNull();
        overview.Lifetime.Summary.PeakDayDate.Should().BeNull();
        overview.Lifetime.Heatmap.Days.Should().HaveCount(364).And.OnlyContain(day => day.TotalTokens == 0);
        overview.Lifetime.Heatmap.MaxTokens.Should().Be(0);
        overview.RangeDaily.Should().HaveCount(30);
        overview.RangeModels.Should().BeEmpty();
        overview.RangeTools.Should().BeEmpty();
    }

    [Fact]
    public async Task ReadAsync_sums_one_model_per_local_day_and_fills_the_rest_of_the_window()
    {
        using var fixture = new ConversationPersistenceFixture();
        var conversation = await SeedConversationAsync(fixture);
        var today = Today(Utc);
        await SeedTurnAsync(fixture, conversation, Midday(today), "model-a", inputTokens: 100, outputTokens: 20);
        await SeedTurnAsync(fixture, conversation, Midday(today).AddHours(1), "model-a", inputTokens: 100, outputTokens: 20);

        var overview = await new SqliteUsageOverviewReader(fixture.Database).ReadAsync(Query(UsageRange.SevenDays));

        var todayPoint = overview.RangeDaily.Single(point => point.Date == today);
        todayPoint.TotalTokens.Should().Be(240);
        todayPoint.InputTokens.Should().Be(200);
        todayPoint.OutputTokens.Should().Be(40);
        todayPoint.TurnCount.Should().Be(2);
        todayPoint.Models.Should().ContainSingle().Which.Should().Be(new UsageDailyModelCount("model-a", 240));
        overview.RangeDaily.Should().HaveCount(7).And.OnlyContain(point => point.Date <= today);
        overview.Lifetime.Heatmap.Days.Single(day => day.Date == today).TurnCount.Should().Be(2);
        overview.Lifetime.Summary.ActiveDays.Should().Be(1);
        overview.Lifetime.Summary.FavoriteModel.Should().Be(new UsageFavoriteModel("model-a", 240, 1d));
    }

    [Fact]
    public async Task ReadAsync_ranks_models_by_tokens_with_their_share_of_the_window()
    {
        using var fixture = new ConversationPersistenceFixture();
        var conversation = await SeedConversationAsync(fixture);
        var today = Today(Utc);
        await SeedTurnAsync(fixture, conversation, Midday(today), "model-big", inputTokens: 300, outputTokens: 100);
        await SeedTurnAsync(fixture, conversation, Midday(today), "model-small", inputTokens: 100, outputTokens: 0);
        await SeedTurnAsync(fixture, conversation, Midday(today), "model-zero", inputTokens: 0, outputTokens: 0, totalTokens: 0);

        var overview = await new SqliteUsageOverviewReader(fixture.Database).ReadAsync(Query(UsageRange.SevenDays));

        // A zero-token model carries no share and must not appear in the ranking.
        overview.RangeModels.Select(model => model.Model).Should().Equal("model-big", "model-small");
        overview.RangeModels[0].Share.Should().BeApproximately(0.8, 0.0001);
        overview.RangeModels[0].TurnCount.Should().Be(1);
        overview.RangeModels[1].Share.Should().BeApproximately(0.2, 0.0001);
        overview.Lifetime.Summary.FavoriteModel!.Model.Should().Be("model-big");
    }

    [Fact]
    public async Task ReadAsync_keeps_a_missing_model_blank_and_falls_back_to_input_plus_output()
    {
        using var fixture = new ConversationPersistenceFixture();
        var conversation = await SeedConversationAsync(fixture);
        await SeedTurnAsync(fixture, conversation, Midday(Today(Utc)), model: null,
            inputTokens: 100, outputTokens: 25, totalTokens: null);

        var overview = await new SqliteUsageOverviewReader(fixture.Database).ReadAsync(Query(UsageRange.SevenDays));

        overview.Lifetime.Summary.TotalTokens.Should().Be(125);
        overview.RangeModels.Should().ContainSingle().Which.Model.Should().Be(BlankModel);
        overview.RangeDaily.Single(point => point.Date == Today(Utc)).Models
            .Should().ContainSingle().Which.Model.Should().Be(BlankModel);
    }

    [Fact]
    public async Task ReadAsync_uses_the_same_cache_hit_rate_formula_as_the_conversation_reader()
    {
        using var fixture = new ConversationPersistenceFixture();
        var conversation = await SeedConversationAsync(fixture);
        await SeedTurnAsync(fixture, conversation, Midday(Today(Utc)), "model-a",
            inputTokens: 100, outputTokens: 10, cachedInputTokens: 40, uncachedInputTokens: 60);

        var overview = await new SqliteUsageOverviewReader(fixture.Database).ReadAsync(Query(UsageRange.SevenDays));
        var report = await new SqliteUsageStatisticsReader(fixture.Database).GetConversationUsageAsync(conversation);

        overview.Lifetime.Summary.CacheHitRate.Should().BeApproximately(0.4, 0.0001);
        overview.Lifetime.Summary.CacheHitRate.Should().Be(report.CacheHitRate);
        overview.Lifetime.Summary.CachedInputTokens.Should().Be(40);
    }

    [Fact]
    public async Task ReadAsync_buckets_by_the_callers_time_zone_not_by_utc()
    {
        using var fixture = new ConversationPersistenceFixture();
        var conversation = await SeedConversationAsync(fixture);
        var zone = TimeZoneInfo.CreateCustomTimeZone("SelfClawTestPlus8", TimeSpan.FromHours(8), "t+8", "t+8");
        var localToday = Today(zone);
        // 00:30 local time is still the previous UTC day.
        var startedAtUtc = new DateTimeOffset(localToday.ToDateTime(new TimeOnly(0, 30)), TimeSpan.FromHours(8))
            .ToUniversalTime();
        await SeedTurnAsync(fixture, conversation, startedAtUtc, "model-a", inputTokens: 50, outputTokens: 5);

        var overview = await new SqliteUsageOverviewReader(fixture.Database)
            .ReadAsync(new UsageOverviewQuery(UsageRange.SevenDays, zone));

        overview.Lifetime.Heatmap.Days.Single(day => day.Date == localToday).TotalTokens.Should().Be(55);
        overview.Lifetime.Heatmap.Days.Single(day => day.Date == localToday.AddDays(-1)).TotalTokens.Should().Be(0);
        overview.TimeZone.Should().Be(zone.Id);
    }

    [Fact]
    public async Task ReadAsync_counts_the_streak_from_today_when_today_is_active()
    {
        using var fixture = new ConversationPersistenceFixture();
        var conversation = await SeedConversationAsync(fixture);
        var today = Today(Utc);
        await SeedTurnAsync(fixture, conversation, Midday(today), "model-a", inputTokens: 10, outputTokens: 0);
        await SeedTurnAsync(fixture, conversation, Midday(today.AddDays(-1)), "model-a", inputTokens: 10, outputTokens: 0);
        await SeedTurnAsync(fixture, conversation, Midday(today.AddDays(-4)), "model-a", inputTokens: 10, outputTokens: 0);

        var overview = await new SqliteUsageOverviewReader(fixture.Database).ReadAsync(Query(UsageRange.ThirtyDays));

        overview.Lifetime.Summary.CurrentStreakDays.Should().Be(2);
        overview.Lifetime.Summary.LongestStreakDays.Should().Be(2);
        overview.Lifetime.Summary.ActiveDays.Should().Be(3);
        overview.Lifetime.Summary.LastActivityDate.Should().Be(today);
        overview.Lifetime.Summary.FirstActivityDate.Should().Be(today.AddDays(-4));
    }

    [Fact]
    public async Task ReadAsync_keeps_the_streak_alive_when_today_is_still_idle()
    {
        using var fixture = new ConversationPersistenceFixture();
        var conversation = await SeedConversationAsync(fixture);
        var today = Today(Utc);
        await SeedTurnAsync(fixture, conversation, Midday(today.AddDays(-1)), "model-a", inputTokens: 10, outputTokens: 0);
        await SeedTurnAsync(fixture, conversation, Midday(today.AddDays(-2)), "model-a", inputTokens: 10, outputTokens: 0);

        var overview = await new SqliteUsageOverviewReader(fixture.Database).ReadAsync(Query(UsageRange.ThirtyDays));

        // Counting from yesterday keeps the streak from dropping to zero at local midnight.
        overview.Lifetime.Summary.CurrentStreakDays.Should().Be(2);
    }

    [Fact]
    public async Task ReadAsync_reports_a_broken_streak_as_one()
    {
        using var fixture = new ConversationPersistenceFixture();
        var conversation = await SeedConversationAsync(fixture);
        var today = Today(Utc);
        await SeedTurnAsync(fixture, conversation, Midday(today), "model-a", inputTokens: 10, outputTokens: 0);
        await SeedTurnAsync(fixture, conversation, Midday(today.AddDays(-2)), "model-a", inputTokens: 10, outputTokens: 0);

        var overview = await new SqliteUsageOverviewReader(fixture.Database).ReadAsync(Query(UsageRange.ThirtyDays));

        overview.Lifetime.Summary.CurrentStreakDays.Should().Be(1);
        overview.Lifetime.Summary.LongestStreakDays.Should().Be(1);
    }

    [Fact]
    public async Task ReadAsync_reports_the_peak_day_and_the_longest_turn()
    {
        using var fixture = new ConversationPersistenceFixture();
        var conversation = await SeedConversationAsync(fixture);
        var today = Today(Utc);
        await SeedTurnAsync(fixture, conversation, Midday(today), "model-a", inputTokens: 10, outputTokens: 0, duration: TimeSpan.FromSeconds(4));
        await SeedTurnAsync(fixture, conversation, Midday(today.AddDays(-1)), "model-a", inputTokens: 900, outputTokens: 0);

        var overview = await new SqliteUsageOverviewReader(fixture.Database).ReadAsync(Query(UsageRange.ThirtyDays));

        overview.Lifetime.Summary.PeakDayTokens.Should().Be(900);
        overview.Lifetime.Summary.PeakDayDate.Should().Be(today.AddDays(-1));
        // julianday works on doubles; a second of slack keeps the assertion about the query, not the FP noise.
        overview.Lifetime.Summary.LongestTurnMs.Should().BeInRange(3_900, 4_100);
    }

    [Fact]
    public async Task ReadAsync_aggregates_tool_runs_with_error_rate_and_average_duration()
    {
        using var fixture = new ConversationPersistenceFixture();
        var conversation = await SeedConversationAsync(fixture);
        var today = Today(Utc);
        await SeedToolRunAsync(fixture, conversation, Midday(today), "search_text", "搜索文本", 1, 100);
        await SeedToolRunAsync(fixture, conversation, Midday(today).AddMinutes(5), "search_text", null, 2, 300);
        await SeedToolRunAsync(fixture, conversation, Midday(today).AddMinutes(9), "read_file", null, 1, 50);

        var overview = await new SqliteUsageOverviewReader(fixture.Database).ReadAsync(Query(UsageRange.SevenDays));

        overview.RangeTools.Should().HaveCount(2);
        var search = overview.RangeTools[0];
        search.ToolName.Should().Be("search_text");
        search.DisplayName.Should().Be("搜索文本");
        search.CallCount.Should().Be(2);
        search.ErrorCount.Should().Be(1);
        search.ErrorRate.Should().BeApproximately(0.5, 0.0001);
        search.AvgDurationMs.Should().BeApproximately(200, 0.0001);
        overview.RangeTools[1].DisplayName.Should().Be("read_file");
        overview.Lifetime.Summary.ToolCallCount.Should().Be(3);
        overview.Lifetime.Summary.ToolErrorRate.Should().BeApproximately(1d / 3d, 0.0001);
        overview.Lifetime.Heatmap.Days.Single(day => day.Date == today).ToolCallCount.Should().Be(3);
    }

    [Fact]
    public async Task ReadAsync_keeps_turns_older_than_the_range_in_the_lifetime_sections_only()
    {
        using var fixture = new ConversationPersistenceFixture();
        var conversation = await SeedConversationAsync(fixture);
        var today = Today(Utc);
        await SeedTurnAsync(fixture, conversation, Midday(today), "model-a", inputTokens: 10, outputTokens: 0);
        await SeedTurnAsync(fixture, conversation, Midday(today.AddDays(-10)), "model-old", inputTokens: 700, outputTokens: 0);

        var overview = await new SqliteUsageOverviewReader(fixture.Database).ReadAsync(Query(UsageRange.SevenDays));

        overview.RangeModels.Should().ContainSingle().Which.Model.Should().Be("model-a");
        overview.RangeDaily.Should().OnlyContain(point => point.TotalTokens == 0 || point.TotalTokens == 10);
        overview.Lifetime.Summary.TotalTokens.Should().Be(710);
        overview.Lifetime.Summary.TotalTurns.Should().Be(2);
        overview.Lifetime.Heatmap.Days.Single(day => day.Date == today.AddDays(-10)).TotalTokens.Should().Be(700);
    }

    [Fact]
    public async Task ReadAsync_starts_the_window_at_local_midnight()
    {
        using var fixture = new ConversationPersistenceFixture();
        var conversation = await SeedConversationAsync(fixture);
        var today = Today(Utc);
        var firstDayInWindow = today.AddDays(-6);
        await SeedTurnAsync(fixture, conversation, Midday(firstDayInWindow), "model-a", inputTokens: 10, outputTokens: 0);
        await SeedTurnAsync(fixture, conversation,
            new DateTimeOffset(firstDayInWindow.ToDateTime(new TimeOnly(0, 0)).AddSeconds(-1), TimeSpan.Zero),
            "model-b", inputTokens: 500, outputTokens: 0);

        var overview = await new SqliteUsageOverviewReader(fixture.Database).ReadAsync(Query(UsageRange.SevenDays));

        overview.RangeDaily[0].Date.Should().Be(firstDayInWindow);
        overview.RangeDaily.Should().OnlyContain(point => point.TotalTokens == 0 || point.TotalTokens == 10);
        overview.Lifetime.Summary.TotalTokens.Should().Be(510);
    }

    [Fact]
    public async Task ReadAsync_counts_subagent_and_automation_conversations_in_the_same_totals()
    {
        using var fixture = new ConversationPersistenceFixture();
        var interactive = await SeedConversationAsync(fixture);
        var subagent = await SeedConversationAsync(fixture, ConversationKind.Subagent, interactive);
        var today = Today(Utc);
        await SeedTurnAsync(fixture, interactive, Midday(today), "model-a", inputTokens: 100, outputTokens: 0);
        await SeedTurnAsync(fixture, subagent, Midday(today), "model-a", inputTokens: 30, outputTokens: 0);

        var overview = await new SqliteUsageOverviewReader(fixture.Database).ReadAsync(Query(UsageRange.SevenDays));

        overview.Lifetime.Summary.TotalTokens.Should().Be(130);
        overview.Lifetime.Summary.TotalSessions.Should().Be(2);
    }

    [Fact]
    public async Task ReadAsync_ignores_a_future_dated_turn_when_counting_the_streak()
    {
        using var fixture = new ConversationPersistenceFixture();
        var conversation = await SeedConversationAsync(fixture);
        var today = Today(Utc);
        await SeedTurnAsync(fixture, conversation, Midday(today), "model-a", inputTokens: 10, outputTokens: 0);
        await SeedTurnAsync(fixture, conversation, Midday(today.AddDays(-1)), "model-a", inputTokens: 10, outputTokens: 0);
        // A clock rollback or a cross-zone write can leave a future-dated row. If that row becomes the
        // newest day, the streak stops matching today/yesterday and silently reports zero.
        await SeedTurnAsync(fixture, conversation, Midday(today.AddDays(3)), "model-a", inputTokens: 10, outputTokens: 0);

        var overview = await new SqliteUsageOverviewReader(fixture.Database).ReadAsync(Query(UsageRange.ThirtyDays));

        overview.Lifetime.Summary.CurrentStreakDays.Should().Be(2);
        overview.Lifetime.Summary.LastActivityDate.Should().Be(today);
    }
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.CreateCustomTimeZone("SelfClawTestUtc", TimeSpan.Zero, "utc", "utc");

    private static UsageOverviewQuery Query(UsageRange range) => new(range, Utc);

    private static DateOnly Today(TimeZoneInfo zone)
        => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, zone).DateTime);

    private static DateTimeOffset Midday(DateOnly date)
        => new(date.ToDateTime(new TimeOnly(12, 0)), TimeSpan.Zero);

    private static async Task<Guid> SeedConversationAsync(
        ConversationPersistenceFixture fixture,
        ConversationKind kind = ConversationKind.Interactive,
        Guid? parentConversationId = null)
    {
        var conversationId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow.ToString("O");
        await fixture.ExecuteAsync(
            """
            INSERT INTO conversations(id, title, created_at_utc, updated_at_utc, kind, parent_conversation_id)
            VALUES($id, 'usage', $at, $at, $kind, $parent);
            """,
            ("$id", conversationId.ToString("D")),
            ("$at", now),
            ("$kind", (int)kind),
            ("$parent", parentConversationId is { } parent ? parent.ToString("D") : DBNull.Value));
        return conversationId;
    }

    private static async Task SeedTurnAsync(
        ConversationPersistenceFixture fixture,
        Guid conversationId,
        DateTimeOffset startedAtUtc,
        string? model,
        long? inputTokens,
        long? outputTokens,
        long? totalTokens = null,
        long? cachedInputTokens = null,
        long? uncachedInputTokens = null,
        TimeSpan? duration = null)
    {
        var turnId = Guid.NewGuid();
        var completedAtUtc = startedAtUtc + (duration ?? TimeSpan.FromSeconds(1));
        await fixture.ExecuteAsync(
            """
            INSERT INTO conversation_turns(id, conversation_id, execution_mode, origin, status, started_at_utc, completed_at_utc)
            VALUES($id, $conversation, 0, 0, 1, $started, $completed);
            """,
            ("$id", turnId.ToString("D")),
            ("$conversation", conversationId.ToString("D")),
            ("$started", startedAtUtc.ToString("O")),
            ("$completed", completedAtUtc.ToString("O")));
        await fixture.ExecuteAsync(
            """
            INSERT INTO turn_usage(turn_id, conversation_id, model, input_tokens, uncached_input_tokens,
                cached_input_tokens, output_tokens, total_tokens, created_at_utc)
            VALUES($turn, $conversation, $model, $input, $uncached, $cached, $output, $total, $at);
            """,
            ("$turn", turnId.ToString("D")),
            ("$conversation", conversationId.ToString("D")),
            ("$model", (object?)model ?? DBNull.Value),
            ("$input", (object?)inputTokens ?? DBNull.Value),
            ("$uncached", (object?)uncachedInputTokens ?? DBNull.Value),
            ("$cached", (object?)cachedInputTokens ?? DBNull.Value),
            ("$output", (object?)outputTokens ?? DBNull.Value),
            ("$total", (object?)totalTokens ?? DBNull.Value),
            ("$at", startedAtUtc.ToString("O")));
    }

    private static async Task SeedToolRunAsync(
        ConversationPersistenceFixture fixture,
        Guid conversationId,
        DateTimeOffset createdAtUtc,
        string toolName,
        string? displayName,
        int status,
        double? durationMs)
    {
        var turnId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var sequence = Convert.ToInt64(await fixture.ScalarAsync(
            "SELECT COALESCE(MAX(sequence), 0) + 1 FROM messages WHERE conversation_id = $conversation;",
            ("$conversation", conversationId.ToString("D"))) ?? 1L);
        var at = createdAtUtc.ToString("O");
        await fixture.ExecuteAsync(
            """
            INSERT INTO conversation_turns(id, conversation_id, execution_mode, origin, status, started_at_utc, completed_at_utc)
            VALUES($id, $conversation, 0, 0, 1, $at, $at);
            """,
            ("$id", turnId.ToString("D")),
            ("$conversation", conversationId.ToString("D")),
            ("$at", at));
        await fixture.ExecuteAsync(
            """
            INSERT INTO messages(id, conversation_id, turn_id, sequence, role, markdown_content, status,
                created_at_utc, updated_at_utc)
            VALUES($id, $conversation, $turn, $sequence, 2, '', 0, $at, $at);
            """,
            ("$id", messageId.ToString("D")),
            ("$conversation", conversationId.ToString("D")),
            ("$turn", turnId.ToString("D")),
            ("$sequence", sequence),
            ("$at", at));
        await fixture.ExecuteAsync(
            """
            INSERT INTO tool_runs(id, conversation_id, tool_name, arguments_json, status, duration_ms,
                created_at_utc, updated_at_utc, message_id, display_name)
            VALUES($id, $conversation, $tool, '{}', $status, $duration, $at, $at, $message, $display);
            """,
            ("$id", Guid.NewGuid().ToString("D")),
            ("$conversation", conversationId.ToString("D")),
            ("$tool", toolName),
            ("$status", status),
            ("$duration", (object?)durationMs ?? DBNull.Value),
            ("$at", at),
            ("$message", messageId.ToString("D")),
            ("$display", (object?)displayName ?? DBNull.Value));
    }
}
