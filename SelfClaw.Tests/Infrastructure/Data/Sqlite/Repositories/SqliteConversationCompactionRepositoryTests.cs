using FluentAssertions;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Infrastructure.Data.Sqlite.Repositories;
using SelfClaw.Tests.TestDoubles;

namespace SelfClaw.Tests.Infrastructure.Data.Sqlite.Repositories;

public sealed class SqliteConversationCompactionRepositoryTests
{
    [Fact]
    public async Task ListAsync_returns_compactions_in_creation_order()
    {
        using var fixture = new ConversationPersistenceFixture();
        var started = await fixture.StartAsync();
        var repository = new SqliteConversationCompactionRepository(fixture.Database);
        var first = await SeedAsync(fixture, started.Turn, 2, "first", 0);
        var second = await SeedAsync(fixture, started.Turn, 6, "second", 1);
        var third = await SeedAsync(fixture, started.Turn, 9, "third", 2);

        var compactions = await repository.ListAsync(started.Turn.ConversationId);

        compactions.Select(compaction => compaction.Id).Should().Equal(first.Id, second.Id, third.Id);
        compactions.Select(compaction => compaction.ThroughSequence).Should().Equal(2L, 6L, 9L);
    }

    [Fact]
    public async Task GetLatestAsync_returns_the_newest_compaction()
    {
        using var fixture = new ConversationPersistenceFixture();
        var started = await fixture.StartAsync();
        var repository = new SqliteConversationCompactionRepository(fixture.Database);
        await SeedAsync(fixture, started.Turn, 2, "first", 0);
        var latest = await SeedAsync(fixture, started.Turn, 6, "second", 1);

        var compaction = await repository.GetLatestAsync(started.Turn.ConversationId);

        compaction.Should().Be(latest);
    }

    [Fact]
    public async Task GetLatestAsync_prefers_the_furthest_boundary_over_the_newest_timestamp()
    {
        using var fixture = new ConversationPersistenceFixture();
        var started = await fixture.StartAsync();
        var repository = new SqliteConversationCompactionRepository(fixture.Database);
        var furthest = await SeedAsync(fixture, started.Turn, 9, "furthest boundary", 0);
        await SeedAsync(fixture, started.Turn, 4, "later timestamp", 10);

        var compaction = await repository.GetLatestAsync(started.Turn.ConversationId);

        compaction.Should().Be(furthest, "replay is bounded by the sequence, not by clock order");
    }

    [Fact]
    public async Task Deleting_the_conversation_cascades_to_its_compactions()
    {
        using var fixture = new ConversationPersistenceFixture();
        var started = await fixture.StartAsync();
        var repository = new SqliteConversationCompactionRepository(fixture.Database);
        await SeedAsync(fixture, started.Turn, 2, "first", 0);
        await SeedAsync(fixture, started.Turn, 6, "second", 1);

        await fixture.ExecuteAsync("DELETE FROM conversations WHERE id = $id;",
            ("$id", started.Turn.ConversationId.ToString("D")));

        (await repository.ListAsync(started.Turn.ConversationId)).Should().BeEmpty();
        (await fixture.ScalarAsync("SELECT COUNT(*) FROM conversation_compactions;")).Should().Be(0L);
    }

    [Fact]
    public async Task Deleting_the_turn_cascades_to_its_compactions()
    {
        using var fixture = new ConversationPersistenceFixture();
        var started = await fixture.StartAsync();
        var repository = new SqliteConversationCompactionRepository(fixture.Database);
        await SeedAsync(fixture, started.Turn, 2, "first", 0);

        await fixture.ExecuteAsync("DELETE FROM conversation_turns WHERE id = $id;",
            ("$id", started.Turn.Id.ToString("D")));

        (await repository.ListAsync(started.Turn.ConversationId)).Should().BeEmpty();
        (await fixture.ScalarAsync("SELECT COUNT(*) FROM conversation_compactions;")).Should().Be(0L);
    }

    [Fact]
    public async Task Nullable_columns_round_trip_as_null()
    {
        using var fixture = new ConversationPersistenceFixture();
        var started = await fixture.StartAsync();
        var repository = new SqliteConversationCompactionRepository(fixture.Database);
        var id = Guid.NewGuid();
        await fixture.ExecuteAsync("""
            INSERT INTO conversation_compactions(id, conversation_id, turn_id, through_sequence, summary, model,
                source_message_count, source_tokens, summary_tokens, created_at_utc)
            VALUES($id, $conversationId, $turnId, 4, 'summary', NULL, 3, NULL, NULL, $createdAt);
            """,
            ("$id", id.ToString("D")),
            ("$conversationId", started.Turn.ConversationId.ToString("D")),
            ("$turnId", started.Turn.Id.ToString("D")),
            ("$createdAt", DateTimeOffset.UtcNow.ToString("O")));

        var compaction = (await repository.ListAsync(started.Turn.ConversationId)).Single();

        compaction.Id.Should().Be(id);
        compaction.Model.Should().BeNull();
        compaction.SourceTokens.Should().BeNull();
        compaction.SummaryTokens.Should().BeNull();
        compaction.SourceMessageCount.Should().Be(3);
        compaction.ThroughSequence.Should().Be(4);
    }

    [Fact]
    public async Task Turn_usage_upsert_keeps_last_request_input_tokens_by_turn_id()
    {
        using var fixture = new ConversationPersistenceFixture();
        var started = await fixture.StartAsync();
        var running = started.Turn with { Usage = new TurnUsage(InputTokens: 10, LastRequestInputTokens: 900) };
        await fixture.Turns.CommitProgressAsync(new ConversationTurnCommit(running, [], []));

        // A later write that observed no input size must not erase an already persisted measurement.
        await fixture.Turns.CommitProgressAsync(
            new ConversationTurnCommit(running with { Usage = new TurnUsage(InputTokens: 15) }, [], []));
        (await ReadLastRequestInputTokensAsync(fixture, running.ConversationId)).Should().Be(900);

        var completed = running with
        {
            Status = ConversationTurnStatus.Succeeded,
            CompletedAtUtc = DateTimeOffset.UtcNow,
            Usage = new TurnUsage(InputTokens: 20, ContextTokens: 950, LastRequestInputTokens: 1234)
        };
        (await fixture.Turns.TryFinalizeTurnAsync(new ConversationTurnCommit(completed, [], []))).Should().BeTrue();

        (await ReadLastRequestInputTokensAsync(fixture, running.ConversationId)).Should().Be(1234);
        var usage = (await fixture.Turns.ListTurnsAsync(running.ConversationId)).Single().Usage;
        usage!.InputTokens.Should().Be(20);
        usage.ContextTokens.Should().Be(950);
        usage.LastRequestInputTokens.Should().Be(1234);
    }

    private static async Task<int?> ReadLastRequestInputTokensAsync(ConversationPersistenceFixture fixture, Guid conversationId)
    {
        var turns = await fixture.Turns.ListTurnsAsync(conversationId);
        return turns.Single().Usage?.LastRequestInputTokens;
    }

    private static async Task<ConversationCompactionRecord> SeedAsync(
        ConversationPersistenceFixture fixture,
        ConversationTurnRecord turn,
        long throughSequence,
        string summary,
        int minuteOffset)
    {
        var compaction = new ConversationCompactionRecord(
            Guid.NewGuid(),
            turn.ConversationId,
            turn.Id,
            throughSequence,
            summary,
            "test-model",
            SourceMessageCount: 2,
            SourceTokens: 4_000,
            SummaryTokens: 120,
            DateTimeOffset.UtcNow.AddMinutes(minuteOffset));
        await fixture.ExecuteAsync("""
            INSERT INTO conversation_compactions(id, conversation_id, turn_id, through_sequence, summary, model,
                source_message_count, source_tokens, summary_tokens, created_at_utc)
            VALUES($id, $conversationId, $turnId, $throughSequence, $summary, $model,
                $sourceMessageCount, $sourceTokens, $summaryTokens, $createdAt);
            """,
            ("$id", compaction.Id.ToString("D")),
            ("$conversationId", compaction.ConversationId.ToString("D")),
            ("$turnId", compaction.TurnId.ToString("D")),
            ("$throughSequence", compaction.ThroughSequence),
            ("$summary", compaction.Summary),
            ("$model", compaction.Model ?? (object)DBNull.Value),
            ("$sourceMessageCount", compaction.SourceMessageCount),
            ("$sourceTokens", compaction.SourceTokens ?? (object)DBNull.Value),
            ("$summaryTokens", compaction.SummaryTokens ?? (object)DBNull.Value),
            ("$createdAt", compaction.CreatedAtUtc.ToString("O")));
        return compaction;
    }
}
