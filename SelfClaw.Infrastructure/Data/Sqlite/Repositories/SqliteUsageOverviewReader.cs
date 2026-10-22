using System.Globalization;
using Microsoft.Data.Sqlite;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Infrastructure.Data.Sqlite;

namespace SelfClaw.Infrastructure.Data.Sqlite.Repositories;

/// <summary>
/// Application-wide usage overview for the About dashboard. SQL only filters ranges and diffs
/// timestamps: <c>..._at_utc</c> is written as <see cref="DateTimeOffset.ToString(string)"/> with seven
/// fractional digits and an offset, which SQLite's <c>date()</c>/<c>strftime()</c> is not trusted to
/// parse — a silent NULL there would blank the whole heatmap. Every local-day bucket is computed here.
/// </summary>
internal sealed class SqliteUsageOverviewReader : IUsageOverviewReader
{
    private const int HeatmapDayCount = 364;
    private const int ToolRankingLimit = 20;
    private const int FailedToolStatus = (int)ToolExecutionStatus.Failed;

    private readonly SqliteDatabase _database;

    public SqliteUsageOverviewReader(SqliteDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        _database = database;
    }

    public async Task<UsageOverview> ReadAsync(UsageOverviewQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var zone = query.TimeZone;
        var today = LocalDate(DateTimeOffset.UtcNow, zone);
        var rangeStart = today.AddDays(1 - (int)query.Range);
        var heatmapStart = today.AddDays(1 - HeatmapDayCount);
        var heatmapStartUtc = StartOfLocalDayUtc(heatmapStart, zone);

        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var lifetimeTotals = await ReadLifetimeTotalsAsync(connection, cancellationToken).ConfigureAwait(false);
        var lifetimeModels = await ReadLifetimeModelTotalsAsync(connection, cancellationToken).ConfigureAwait(false);
        var lifetimeDayRows = await ReadLifetimeDayRowsAsync(connection, cancellationToken).ConfigureAwait(false);
        var windowTurns = await ReadWindowTurnsAsync(connection, heatmapStartUtc, cancellationToken).ConfigureAwait(false);
        var toolCalls = await ReadToolCallsAsync(connection, heatmapStartUtc, cancellationToken).ConfigureAwait(false);
        var lifetimeTools = await ReadLifetimeToolTotalsAsync(connection, cancellationToken).ConfigureAwait(false);
        var longestTurnMs = await ReadLongestTurnMsAsync(connection, cancellationToken).ConfigureAwait(false);

        // Lifetime buckets only need day + tokens; the wide per-turn columns stay inside the bounded
        // window that actually renders them.
        var lifetimeDayTokens = GroupDayTokens(lifetimeDayRows, zone);
        var windowDays = GroupDays(windowTurns, zone);
        var rangeTurns = windowTurns.Where(turn => LocalDate(turn.StartedAtUtc, zone) >= rangeStart).ToArray();
        var streaks = ReadStreaks(lifetimeDayTokens.Keys, today);

        var lifetimeSummary = BuildSummary(
            new WindowTotals(
                lifetimeTotals.InputTokens,
                lifetimeTotals.UncachedInputTokens,
                lifetimeTotals.CachedInputTokens,
                lifetimeTotals.CacheWriteInputTokens,
                lifetimeTotals.OutputTokens,
                lifetimeTotals.ReasoningTokens,
                lifetimeTotals.TotalTokens,
                lifetimeTotals.TurnCount,
                lifetimeTotals.SessionCount),
            lifetimeDayTokens,
            lifetimeModels,
            streaks,
            longestTurnMs,
            lifetimeTools,
            today);

        return new UsageOverview(
            DateTimeOffset.UtcNow,
            zone.Id,
            query.Range,
            new UsageLifetimeSnapshot(lifetimeSummary, BuildHeatmap(windowDays, toolCalls, zone, heatmapStart, today)),
            BuildDailyPoints(windowDays, rangeStart, today),
            BuildModelRanking(rangeTurns),
            BuildToolRanking(toolCalls, zone, rangeStart));
    }

    private static async Task<LifetimeTotals> ReadLifetimeTotalsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*),
                   COUNT(DISTINCT u.conversation_id),
                   COALESCE(SUM(COALESCE(u.input_tokens, 0)), 0),
                   COALESCE(SUM(COALESCE(u.uncached_input_tokens, 0)), 0),
                   COALESCE(SUM(COALESCE(u.cached_input_tokens, 0)), 0),
                   COALESCE(SUM(COALESCE(u.cache_write_input_tokens, 0)), 0),
                   COALESCE(SUM(COALESCE(u.output_tokens, 0)), 0),
                   COALESCE(SUM(COALESCE(u.reasoning_tokens, 0)), 0),
                   COALESCE(SUM(COALESCE(u.total_tokens,
                       COALESCE(u.input_tokens, 0) + COALESCE(u.output_tokens, 0))), 0)
            FROM turn_usage u;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return default;
        }

        return new LifetimeTotals(
            (int)reader.GetInt64(0),
            (int)reader.GetInt64(1),
            reader.GetInt64(2),
            reader.GetInt64(3),
            reader.GetInt64(4),
            reader.GetInt64(5),
            reader.GetInt64(6),
            reader.GetInt64(7),
            reader.GetInt64(8));
    }

    private static async Task<IReadOnlyList<UsageModelTotals>> ReadLifetimeModelTotalsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COALESCE(u.model, '') AS model,
                   COALESCE(SUM(COALESCE(u.total_tokens,
                       COALESCE(u.input_tokens, 0) + COALESCE(u.output_tokens, 0))), 0)
            FROM turn_usage u
            GROUP BY COALESCE(u.model, '');
            """;
        var models = new List<UsageModelTotals>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            models.Add(new UsageModelTotals(reader.GetString(0), reader.GetInt64(1)));
        }

        return models;
    }

    // Lifetime buckets (active days, peak day, streak) need the whole history, but only the day and the
    // token total. Reading the wide per-turn columns for every turn in the database would be waste now
    // that the range sections read their own bounded window.
    private static async Task<IReadOnlyList<UsageDayRow>> ReadLifetimeDayRowsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT t.started_at_utc,
                   COALESCE(u.total_tokens,
                       COALESCE(u.input_tokens, 0) + COALESCE(u.output_tokens, 0))
            FROM turn_usage u
            JOIN conversation_turns t ON t.id = u.turn_id;
            """;
        var rows = new List<UsageDayRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new UsageDayRow(ParseTimestamp(reader.GetString(0)), reader.GetInt64(1)));
        }

        return rows;
    }

    // The same rows feed the trend, the ranking and the heatmap, so the window has to cover the widest
    // of the three: the 52-week heatmap.
    private static async Task<IReadOnlyList<TurnRow>> ReadWindowTurnsAsync(
        SqliteConnection connection,
        string sinceUtc,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT t.started_at_utc,
                   COALESCE(u.model, '') AS model,
                   COALESCE(u.input_tokens, 0),
                   COALESCE(u.uncached_input_tokens, 0),
                   COALESCE(u.cached_input_tokens, 0),
                   COALESCE(u.cache_write_input_tokens, 0),
                   COALESCE(u.output_tokens, 0),
                   COALESCE(u.reasoning_tokens, 0),
                   COALESCE(u.total_tokens,
                       COALESCE(u.input_tokens, 0) + COALESCE(u.output_tokens, 0)),
                   t.completed_at_utc,
                   u.conversation_id
            FROM turn_usage u
            JOIN conversation_turns t ON t.id = u.turn_id
            WHERE t.started_at_utc >= $since
            ORDER BY t.started_at_utc;
            """;
        command.Parameters.AddWithValue("$since", sinceUtc);
        var turns = new List<TurnRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            turns.Add(new TurnRow(
                ParseTimestamp(reader.GetString(0)),
                reader.GetString(1),
                reader.GetInt64(2),
                reader.GetInt64(3),
                reader.GetInt64(4),
                reader.GetInt64(5),
                reader.GetInt64(6),
                reader.GetInt64(7),
                reader.GetInt64(8),
                reader.IsDBNull(9) ? null : ParseTimestamp(reader.GetString(9)),
                Guid.Parse(reader.GetString(10))));
        }

        return turns;
    }

    private static async Task<IReadOnlyList<ToolCallRow>> ReadToolCallsAsync(
        SqliteConnection connection,
        string sinceUtc,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT t.created_at_utc, t.tool_name, t.display_name, t.status, t.duration_ms
            FROM tool_runs t
            WHERE t.created_at_utc >= $since
            ORDER BY t.created_at_utc;
            """;
        command.Parameters.AddWithValue("$since", sinceUtc);

        var calls = new List<ToolCallRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var toolName = reader.GetString(1);
            var displayName = reader.IsDBNull(2) ? string.Empty : reader.GetString(2).Trim();
            calls.Add(new ToolCallRow(
                ParseTimestamp(reader.GetString(0)),
                toolName,
                displayName.Length == 0 ? toolName : displayName,
                (int)reader.GetInt64(3),
                reader.IsDBNull(4) ? null : reader.GetDouble(4)));
        }

        return calls;
    }

    private static async Task<ToolTotals> ReadLifetimeToolTotalsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*),
                   COALESCE(SUM(CASE WHEN t.status = $failed THEN 1 ELSE 0 END), 0)
            FROM tool_runs t;
            """;
        command.Parameters.AddWithValue("$failed", FailedToolStatus);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return default;
        }

        var callCount = (int)reader.GetInt64(0);
        return new ToolTotals(callCount, ToolErrorRate(callCount, (int)reader.GetInt64(1)));
    }

    // julianday only diffs two timestamps here — it never produces a date string, so it sidesteps the
    // parsing risk that keeps day bucketing in C#.
    private static async Task<long> ReadLongestTurnMsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT MAX((julianday(t.completed_at_utc) - julianday(t.started_at_utc)) * 86400000)
            FROM conversation_turns t
            WHERE t.completed_at_utc IS NOT NULL;
            """;
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value switch
        {
            double milliseconds when double.IsFinite(milliseconds) => Math.Max(0L, (long)milliseconds),
            long milliseconds => Math.Max(0L, milliseconds),
            _ => 0L
        };
    }

    private static UsageSummary BuildSummary(
        WindowTotals totals,
        IReadOnlyDictionary<DateOnly, long> dayTokens,
        IReadOnlyList<UsageModelTotals> models,
        StreakDays streaks,
        long longestTurnMs,
        ToolTotals tools,
        DateOnly today)
    {
        // Date-shaped metrics stop at today: a future-dated row would otherwise become "last activity"
        // and could win the peak day. The token totals stay as recorded — that usage did happen.
        var activeDays = dayTokens.Keys.Where(date => date <= today).Order().ToArray();
        var hasActivity = activeDays.Length > 0;
        var peak = activeDays
            .Select(date => new KeyValuePair<DateOnly, long>(date, dayTokens[date]))
            .OrderByDescending(day => day.Value)
            .ThenByDescending(day => day.Key)
            .FirstOrDefault();

        return new UsageSummary(
            totals.TotalTokens,
            totals.InputTokens,
            totals.OutputTokens,
            totals.ReasoningTokens,
            totals.CachedInputTokens,
            totals.CacheWriteInputTokens,
            CacheHitRate(totals.CachedInputTokens, totals.UncachedInputTokens),
            totals.TurnCount,
            totals.SessionCount,
            activeDays.Length,
            streaks.Current,
            streaks.Longest,
            longestTurnMs,
            hasActivity ? peak.Value : 0,
            hasActivity ? peak.Key : null,
            FavoriteModel(models, totals.TotalTokens),
            tools.CallCount,
            tools.ErrorRate,
            hasActivity ? activeDays[0] : null,
            hasActivity ? activeDays[^1] : null);
    }

    private static UsageHeatmap BuildHeatmap(
        IReadOnlyDictionary<DateOnly, DayTotals> days,
        IReadOnlyList<ToolCallRow> toolCalls,
        TimeZoneInfo zone,
        DateOnly start,
        DateOnly end)
    {
        var toolCallsByDay = toolCalls
            .GroupBy(call => LocalDate(call.CreatedAtUtc, zone))
            .ToDictionary(group => group.Key, group => group.Count());

        var heatmapDays = new List<UsageHeatmapDay>();
        var maxTokens = 0L;
        for (var date = start; date <= end; date = date.AddDays(1))
        {
            var day = days.GetValueOrDefault(date);
            var tokens = day?.TotalTokens ?? 0;
            maxTokens = Math.Max(maxTokens, tokens);
            heatmapDays.Add(new UsageHeatmapDay(
                date,
                tokens,
                day?.TurnCount ?? 0,
                toolCallsByDay.GetValueOrDefault(date)));
        }

        return new UsageHeatmap(start, end, maxTokens, heatmapDays);
    }

    private static IReadOnlyList<UsageDailyPoint> BuildDailyPoints(
        IReadOnlyDictionary<DateOnly, DayTotals> days,
        DateOnly start,
        DateOnly end)
    {
        var points = new List<UsageDailyPoint>();
        for (var date = start; date <= end; date = date.AddDays(1))
        {
            var day = days.GetValueOrDefault(date);
            points.Add(new UsageDailyPoint(
                date,
                day?.TotalTokens ?? 0,
                day?.InputTokens ?? 0,
                day?.OutputTokens ?? 0,
                day?.TurnCount ?? 0,
                day?.Models ?? []));
        }

        return points;
    }

    private static IReadOnlyList<UsageModelUsage> BuildModelRanking(IReadOnlyList<TurnRow> turns)
    {
        var windowTokens = turns.Sum(turn => turn.TotalTokens);
        return turns
            .GroupBy(turn => turn.Model, StringComparer.Ordinal)
            .Select(group =>
            {
                var tokens = group.Sum(turn => turn.TotalTokens);
                return new UsageModelUsage(
                    group.Key,
                    tokens,
                    group.Sum(turn => turn.InputTokens),
                    group.Sum(turn => turn.OutputTokens),
                    group.Count(),
                    Share(tokens, windowTokens));
            })
            // A zero-share model is noise in a ranking; the design keeps it out of the list.
            .Where(model => model.TotalTokens > 0)
            .OrderByDescending(model => model.TotalTokens)
            .ThenBy(model => model.Model, StringComparer.Ordinal)
            .ToArray();
    }

    private static IReadOnlyList<UsageToolUsage> BuildToolRanking(
        IReadOnlyList<ToolCallRow> calls,
        TimeZoneInfo zone,
        DateOnly rangeStart)
        => calls
            .Where(call => LocalDate(call.CreatedAtUtc, zone) >= rangeStart)
            .GroupBy(call => call.ToolName, StringComparer.Ordinal)
            .Select(group => ToToolUsage(group.Key, group.ToArray()))
            .OrderByDescending(tool => tool.CallCount)
            .ThenBy(tool => tool.ToolName, StringComparer.Ordinal)
            .Take(ToolRankingLimit)
            .ToArray();

    private static UsageToolUsage ToToolUsage(string toolName, IReadOnlyList<ToolCallRow> calls)
    {
        var durations = calls
            .Select(call => call.DurationMs)
            .Where(duration => duration.HasValue)
            .Select(duration => duration.GetValueOrDefault())
            .ToArray();
        var errors = calls.Count(call => call.Status == FailedToolStatus);
        return new UsageToolUsage(
            toolName,
            calls[0].DisplayName,
            calls.Count,
            errors,
            calls.Count > 0 ? (double)errors / calls.Count : 0d,
            durations.Length == 0 ? null : durations.Average());
    }


    // Lifetime day buckets only carry tokens: the per-model split and the input/output breakdown are
    // only rendered for the selected window, so the wide query never touches the whole history.
    private static Dictionary<DateOnly, long> GroupDayTokens(IReadOnlyList<UsageDayRow> rows, TimeZoneInfo zone)
        => rows
            .GroupBy(row => LocalDate(row.StartedAtUtc, zone))
            .ToDictionary(group => group.Key, group => group.Sum(row => row.TotalTokens));

    private static Dictionary<DateOnly, DayTotals> GroupDays(IReadOnlyList<TurnRow> turns, TimeZoneInfo zone)
        => turns
            .GroupBy(turn => LocalDate(turn.StartedAtUtc, zone))
            .ToDictionary(
                group => group.Key,
                group => new DayTotals(
                    group.Sum(turn => turn.TotalTokens),
                    group.Sum(turn => turn.InputTokens),
                    group.Sum(turn => turn.OutputTokens),
                    group.Count(),
                    group
                        .GroupBy(turn => turn.Model, StringComparer.Ordinal)
                        .Select(models => new UsageDailyModelCount(models.Key, models.Sum(turn => turn.TotalTokens)))
                        .OrderByDescending(model => model.TotalTokens)
                        .ThenBy(model => model.Model, StringComparer.Ordinal)
                        .ToArray()));

    // Today with no activity must not drop the streak to zero at midnight, so an idle today falls back
    // to counting from yesterday.
    private static StreakDays ReadStreaks(IEnumerable<DateOnly> activeDates, DateOnly today)
    {
        // A future-dated turn (clock skew, a time zone that moved) would become "the newest day", fail to
        // match today and yesterday, and silently drop the streak to zero.
        var days = activeDates.Where(date => date <= today).Distinct().Order().ToArray();
        if (days.Length == 0)
        {
            return default;
        }

        var longest = 1;
        var run = 1;
        for (var index = 1; index < days.Length; index++)
        {
            run = days[index] == days[index - 1].AddDays(1) ? run + 1 : 1;
            longest = Math.Max(longest, run);
        }

        var current = 0;
        var cursor = days[^1] == today ? today : today.AddDays(-1);
        for (var index = days.Length - 1; index >= 0 && days[index] == cursor; index--, cursor = cursor.AddDays(-1))
        {
            current++;
        }

        return new StreakDays(current, longest);
    }

    private static UsageFavoriteModel? FavoriteModel(IReadOnlyList<UsageModelTotals> models, long totalTokens)
    {
        var favorite = models
            .Where(model => model.TotalTokens > 0)
            .OrderByDescending(model => model.TotalTokens)
            .ThenBy(model => model.Model, StringComparer.Ordinal)
            .FirstOrDefault();
        return favorite is null || totalTokens <= 0
            ? null
            : new UsageFavoriteModel(favorite.Model, favorite.TotalTokens, Share(favorite.TotalTokens, totalTokens));
    }

    // Same formula as SqliteUsageStatisticsReader.CacheHitRate: a cache write counts as a miss, so the
    // denominator is fresh-plus-read input. Two different hit rates in one app would be a bug.
    private static double? CacheHitRate(long cachedInputTokens, long uncachedInputTokens)
    {
        var denominator = cachedInputTokens + uncachedInputTokens;
        return denominator > 0 ? (double)cachedInputTokens / denominator : null;
    }

    private static double? ToolErrorRate(int callCount, int errorCount)
        => callCount > 0 ? (double)errorCount / callCount : null;

    private static double Share(long value, long total) => total > 0 ? (double)value / total : 0d;


    private static DateOnly LocalDate(DateTimeOffset value, TimeZoneInfo zone)
        => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(value, zone).DateTime);

    private static DateTimeOffset ParseTimestamp(string value)
        => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.None);

    // A handful of zones move their clocks at local midnight; falling back to noon keeps the read
    // bounded instead of throwing on an invalid time.
    private static string StartOfLocalDayUtc(DateOnly date, TimeZoneInfo zone)
    {
        var midnight = date.ToDateTime(TimeOnly.MinValue);
        var utc = zone.IsInvalidTime(midnight)
            ? TimeZoneInfo.ConvertTimeToUtc(midnight.AddHours(12), zone)
            : TimeZoneInfo.ConvertTimeToUtc(midnight, zone);
        return new DateTimeOffset(utc, TimeSpan.Zero).ToString("O", CultureInfo.InvariantCulture);
    }

    private readonly record struct LifetimeTotals(
        int TurnCount,
        int SessionCount,
        long InputTokens,
        long UncachedInputTokens,
        long CachedInputTokens,
        long CacheWriteInputTokens,
        long OutputTokens,
        long ReasoningTokens,
        long TotalTokens);

    private readonly record struct WindowTotals(
        long InputTokens,
        long UncachedInputTokens,
        long CachedInputTokens,
        long CacheWriteInputTokens,
        long OutputTokens,
        long ReasoningTokens,
        long TotalTokens,
        int TurnCount,
        int SessionCount);

    private readonly record struct ToolTotals(int CallCount, double? ErrorRate);

    private readonly record struct StreakDays(int Current, int Longest);

    private sealed record DayTotals(
        long TotalTokens,
        long InputTokens,
        long OutputTokens,
        int TurnCount,
        IReadOnlyList<UsageDailyModelCount> Models);

    private sealed record UsageDayRow(DateTimeOffset StartedAtUtc, long TotalTokens);

    private sealed record TurnRow(
        DateTimeOffset StartedAtUtc,
        string Model,
        long InputTokens,
        long UncachedInputTokens,
        long CachedInputTokens,
        long CacheWriteInputTokens,
        long OutputTokens,
        long ReasoningTokens,
        long TotalTokens,
        DateTimeOffset? CompletedAtUtc,
        Guid ConversationId);

    private sealed record ToolCallRow(
        DateTimeOffset CreatedAtUtc,
        string ToolName,
        string DisplayName,
        int Status,
        double? DurationMs);
}
