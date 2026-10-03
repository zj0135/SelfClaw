using Microsoft.Data.Sqlite;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Infrastructure.Data.Sqlite;

namespace SelfClaw.Infrastructure.Data.Sqlite.Repositories;

/// <summary>
/// Reads normalized turn usage from <c>turn_usage</c>. Interactive-conversation usage and Subagent
/// usage are separate reports: the Subagent query only joins child turns owned by the parent, so the
/// parent's own turns are never mixed into it.
/// </summary>
internal sealed class SqliteUsageStatisticsReader : IUsageStatisticsReader
{
    private const string AggregateColumns = """
        COALESCE(u.model, '') AS model,
        COALESCE(SUM(u.input_tokens), 0),
        COALESCE(SUM(u.uncached_input_tokens), 0),
        COALESCE(SUM(u.cached_input_tokens), 0),
        COALESCE(SUM(u.cache_write_input_tokens), 0),
        COALESCE(SUM(u.output_tokens), 0),
        COALESCE(SUM(u.reasoning_tokens), 0),
        COALESCE(SUM(u.total_tokens), 0),
        COALESCE(SUM(u.provider_calls), 0),
        COALESCE(SUM(u.cost_usd_micros), 0),
        COUNT(*),
        COALESCE(SUM(CASE WHEN u.cost_usd_micros IS NOT NULL THEN 1 ELSE 0 END), 0)
        """;

    private readonly SqliteDatabase _database;

    public SqliteUsageStatisticsReader(SqliteDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        _database = database;
    }

    public async Task<UsageReport> GetConversationUsageAsync(
        Guid conversationId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await ReadReportAsync(
                connection,
                $"""
                SELECT {AggregateColumns}
                FROM turn_usage u
                WHERE u.conversation_id = $scope
                GROUP BY COALESCE(u.model, '');
                """,
                conversationId,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<UsageReport> GetSubagentUsageAsync(
        Guid parentConversationId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await ReadReportAsync(
                connection,
                $"""
                SELECT {AggregateColumns}
                FROM turn_usage u
                JOIN subagent_tasks t
                  ON t.child_turn_id = u.turn_id AND t.child_conversation_id = u.conversation_id
                WHERE t.parent_conversation_id = $scope
                GROUP BY COALESCE(u.model, '');
                """,
                parentConversationId,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<UsageReport> ReadReportAsync(
        SqliteConnection connection,
        string sql,
        Guid scopeId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$scope", scopeId.ToString("D"));

        var models = new List<ModelUsageSummary>();
        var inputTokens = 0L;
        var uncachedInputTokens = 0L;
        var cachedInputTokens = 0L;
        var cacheWriteInputTokens = 0L;
        var outputTokens = 0L;
        var reasoningTokens = 0L;
        var totalTokens = 0L;
        var providerCalls = 0L;
        var costUsdMicros = 0L;
        var turnCount = 0;
        var pricedTurnCount = 0;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var modelTotals = new UsageTotals(
                InputTokens: reader.GetInt64(1),
                UncachedInputTokens: reader.GetInt64(2),
                CachedInputTokens: reader.GetInt64(3),
                CacheWriteInputTokens: reader.GetInt64(4),
                OutputTokens: reader.GetInt64(5),
                ReasoningTokens: reader.GetInt64(6),
                TotalTokens: reader.GetInt64(7),
                ProviderCalls: reader.GetInt64(8),
                CostUsdMicros: reader.GetInt64(9),
                TurnCount: (int)reader.GetInt64(10),
                PricedTurnCount: (int)reader.GetInt64(11));
            models.Add(new ModelUsageSummary(reader.GetString(0), modelTotals, CacheHitRate(modelTotals)));

            inputTokens += modelTotals.InputTokens;
            uncachedInputTokens += modelTotals.UncachedInputTokens;
            cachedInputTokens += modelTotals.CachedInputTokens;
            cacheWriteInputTokens += modelTotals.CacheWriteInputTokens;
            outputTokens += modelTotals.OutputTokens;
            reasoningTokens += modelTotals.ReasoningTokens;
            totalTokens += modelTotals.TotalTokens;
            providerCalls += modelTotals.ProviderCalls;
            costUsdMicros += modelTotals.CostUsdMicros;
            turnCount += modelTotals.TurnCount;
            pricedTurnCount += modelTotals.PricedTurnCount;
        }

        var aggregate = new UsageTotals(
            inputTokens,
            uncachedInputTokens,
            cachedInputTokens,
            cacheWriteInputTokens,
            outputTokens,
            reasoningTokens,
            totalTokens,
            providerCalls,
            costUsdMicros,
            turnCount,
            pricedTurnCount);
        return new UsageReport(aggregate, CacheHitRate(aggregate), models);
    }

    /// <summary>Cache write counts as a miss, so the denominator is the fresh-plus-read input.</summary>
    private static double? CacheHitRate(UsageTotals totals)
    {
        var denominator = totals.CachedInputTokens + totals.UncachedInputTokens;
        return denominator > 0 ? (double)totals.CachedInputTokens / denominator : null;
    }
}
