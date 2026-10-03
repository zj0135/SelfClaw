using FluentAssertions;
using Microsoft.Data.Sqlite;
using SelfClaw.Core.Models;
using SelfClaw.Tests.TestDoubles;

namespace SelfClaw.Tests.Infrastructure.Data.Sqlite.Repositories;

public sealed class SqliteConversationInputRepositoryTests
{
    [Fact]
    public async Task Boundaries_order_independent_fragments_users_and_tools_by_sequence_not_timestamp()
    {
        using var fixture = new ConversationPersistenceFixture();
        var start = await fixture.StartAsync("U0");
        var owner = Guid.NewGuid();
        var first = await fixture.AssistantAsync(start.Turn, "A0");
        var firstTool = Tool(first, "first");
        var secondTool = Tool(first, "second");
        first = first with { Status = MessageStatus.Sealed, Segments = [
            new(first.Id, 0, MessageSegmentKind.Text, "A0", null),
            new(first.Id, 1, MessageSegmentKind.ToolCall, null, firstTool.Id),
            new(first.Id, 2, MessageSegmentKind.ToolCall, null, secondTool.Id)] };
        var batch = await fixture.SeedClaimAsync(start.Turn, owner, 1, "U1");
        var usage = new TurnUsage(InputTokens: 10, OutputTokens: 2, TotalTokens: null, ProviderCalls: 1);
        var consumption = await fixture.Inputs.CommitBoundaryAsync(new(batch,
            new(start.Turn with { Usage = usage }, [first], [firstTool, secondTool])));
        var second = await fixture.AssistantAsync(start.Turn, "A1", MessageStatus.Sealed);
        var thirdTool = Tool(second, "third");
        second = second with { Segments = [new(second.Id, 0, MessageSegmentKind.Text, "A1", null), new(second.Id, 1, MessageSegmentKind.ToolCall, null, thirdTool.Id)] };
        var final = start.Turn with { Status = ConversationTurnStatus.Succeeded, CompletedAtUtc = DateTimeOffset.UtcNow, Usage = usage with { InputTokens = 20, OutputTokens = 4, ProviderCalls = 2 } };
        await fixture.Turns.TryFinalizeTurnAsync(new(final, [second], [thirdTool]));
        await fixture.ExecuteAsync("UPDATE messages SET created_at_utc = '2026-01-01T00:00:00+00:00', updated_at_utc = '2026-01-01T00:00:00+00:00';");

        var messages = await fixture.Conversations.ListMessagesAsync(start.Turn.ConversationId);
        messages.Select(message => message.MarkdownContent).Should().Equal("U0", "A0", "U1", "A1");
        messages.Select(message => message.Sequence).Should().Equal(1, 2, 3, 4);
        messages.Select(message => message.Id).Should().OnlyHaveUniqueItems().And.NotContain(start.Turn.Id);
        messages.Should().OnlyContain(message => message.TurnId == start.Turn.Id);
        consumption.Messages.Should().ContainSingle().Which.Id.Should().Be(messages[2].Id);
        var tools = await fixture.Conversations.ListToolExecutionsAsync(start.Turn.ConversationId);
        tools.Count(tool => tool.MessageId == first.Id).Should().Be(2);
        tools.Count(tool => tool.MessageId == second.Id).Should().Be(1);
        (await fixture.ScalarAsync("SELECT COUNT(*) FROM turn_usage;")).Should().Be(1L);
        (await fixture.ScalarAsync("SELECT total_tokens IS NULL FROM turn_usage;")).Should().Be(1L);
    }

    [Fact]
    public async Task Multiple_boundaries_before_first_output_create_no_assistant_and_overwrite_absolute_usage_once()
    {
        using var fixture = new ConversationPersistenceFixture();
        var start = await fixture.StartAsync();
        var owner = Guid.NewGuid();
        var first = await fixture.SeedClaimAsync(start.Turn, owner, 1, "first", "second");
        var firstUsage = new TurnUsage(InputTokens: 10, OutputTokens: 2, TotalTokens: null, ProviderCalls: 1);
        var firstCommit = new ConversationInputBoundaryCommit(first, new(start.Turn with { Usage = firstUsage }, [], []));
        var firstResult = await fixture.Inputs.CommitBoundaryAsync(firstCommit);
        var duplicate = await fixture.Inputs.CommitBoundaryAsync(firstCommit);
        duplicate.Messages.Should().BeEquivalentTo(firstResult.Messages);
        var second = await fixture.SeedClaimAsync(start.Turn, owner, 3, "third");
        var secondUsage = firstUsage with { InputTokens = 30, OutputTokens = 5, ProviderCalls = 2 };
        await fixture.Inputs.CommitBoundaryAsync(new(second, new(start.Turn with { Usage = secondUsage }, [], [])));
        var final = start.Turn with { Status = ConversationTurnStatus.Failed, CompletedAtUtc = DateTimeOffset.UtcNow, Usage = secondUsage with { InputTokens = 40, OutputTokens = 8, ProviderCalls = 3 } };
        await fixture.Turns.TryFinalizeTurnAsync(new(final, [], []));
        (await fixture.Conversations.ListMessagesAsync(final.ConversationId)).Should().HaveCount(4).And.OnlyContain(message => message.Role == MessageRole.User);
        (await fixture.ScalarAsync("SELECT input_tokens FROM turn_usage;")).Should().Be(40L);
        (await fixture.ScalarAsync("SELECT provider_calls FROM turn_usage;")).Should().Be(3L);
        (await fixture.ScalarAsync("SELECT queue_revision FROM conversation_input_state;")).Should().Be(2L);
        (await fixture.Inputs.HasUnprocessedInputsAsync(final.ConversationId)).Should().BeFalse();
        (await fixture.Inputs.ReadConsumptionAsync(first)).Should().NotBeNull();
        (await fixture.ScalarAsync("SELECT COUNT(*) FROM messages WHERE role = 2;")).Should().Be(0L);
    }

    [Fact]
    public async Task Duplicate_confirmation_does_not_reseal_fragment_or_replace_newer_usage()
    {
        using var fixture = new ConversationPersistenceFixture();
        var start = await fixture.StartAsync();
        var batch = await fixture.SeedClaimAsync(start.Turn, Guid.NewGuid(), 1, "steer");
        var first = await fixture.AssistantAsync(start.Turn, "sealed", MessageStatus.Sealed);
        var original = new ConversationInputBoundaryCommit(batch, new(start.Turn with { Usage = new TurnUsage(InputTokens: 10) }, [first], []));
        await fixture.Inputs.CommitBoundaryAsync(original);
        var final = start.Turn with { Status = ConversationTurnStatus.Succeeded, CompletedAtUtc = DateTimeOffset.UtcNow, Usage = new TurnUsage(InputTokens: 30, TotalTokens: null) };
        await fixture.Turns.TryFinalizeTurnAsync(new(final, [], []));
        var replay = await fixture.Inputs.CommitBoundaryAsync(original with
        {
            Content = original.Content with { Messages = [first with { MarkdownContent = "corrupted stale content" }] }
        });
        replay.Turn.Should().Be(final);
        (await fixture.Conversations.ListMessagesAsync(final.ConversationId)).Single(message => message.Id == first.Id).MarkdownContent.Should().Be("sealed");
        (await fixture.ScalarAsync("SELECT input_tokens FROM turn_usage;")).Should().Be(30L);
        (await fixture.ScalarAsync("SELECT next_sequence FROM conversation_message_sequences;")).Should().Be(4L);
    }

    [Fact]
    public async Task Boundary_before_commit_fault_rolls_back_entire_claim_content_usage_and_allocations()
    {
        using var fixture = new ConversationPersistenceFixture((stage, _) =>
            throw new InvalidOperationException(stage));
        var start = await fixture.StartAsync();
        var message = await fixture.AssistantAsync(start.Turn, "partial");
        var tool = Tool(message, "call");
        message = message with { Segments = [new(message.Id, 0, MessageSegmentKind.ToolCall, null, tool.Id)] };
        await fixture.Turns.CommitProgressAsync(new(start.Turn, [message], [tool]));
        var batch = await fixture.SeedClaimAsync(start.Turn, Guid.NewGuid(), 1, "one", "two");
        var boundary = new ConversationInputBoundaryCommit(batch, new(start.Turn with { Usage = new TurnUsage(InputTokens: 5) },
            [message with { Status = MessageStatus.Sealed }], [tool with { Status = ToolExecutionStatus.Failed }]));
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Inputs.CommitBoundaryAsync(boundary));
        exception.Message.Should().Be("boundary-before-commit");
        (await fixture.ScalarAsync("SELECT COUNT(*) FROM conversation_inputs WHERE status = 1;")).Should().Be(2L);
        (await fixture.ScalarAsync("SELECT COUNT(*) FROM messages;")).Should().Be(2L);
        (await fixture.ScalarAsync("SELECT next_sequence FROM conversation_message_sequences;")).Should().Be(3L);
        (await fixture.ScalarAsync("SELECT COUNT(*) FROM turn_usage;")).Should().Be(0L);
        (await fixture.ScalarAsync("SELECT COUNT(*) FROM conversation_input_state;")).Should().Be(0L);
        (await fixture.Conversations.ListMessagesAsync(message.ConversationId)).Last().Status.Should().Be(MessageStatus.Streaming);
        (await fixture.Conversations.ListToolExecutionsAsync(message.ConversationId)).Single().Status.Should().Be(tool.Status);
        (await fixture.Inputs.ReadConsumptionAsync(batch)).Should().BeNull();
    }

    [Fact]
    public async Task Stale_owner_revision_or_incomplete_batch_cannot_consume_any_input()
    {
        using var fixture = new ConversationPersistenceFixture();
        var start = await fixture.StartAsync();
        var owner = Guid.NewGuid();
        var batch = await fixture.SeedClaimAsync(start.Turn, owner, 1, "one", "two");
        var repeated = await fixture.Inputs.ReadClaimedBatchAsync(start.Turn.ConversationId, start.Turn.Id, owner);
        repeated.Should().BeEquivalentTo(batch);
        (await fixture.Inputs.ReadClaimedBatchAsync(start.Turn.ConversationId, start.Turn.Id, Guid.NewGuid())).Should().BeNull();
        var mutations = new[]
        {
            batch with { OwnerRunId = Guid.NewGuid() },
            batch with { Inputs = [batch.Inputs[0] with { Revision = 2 }, batch.Inputs[1]] },
            batch with { Inputs = [batch.Inputs[0]] },
            batch with { TurnId = Guid.NewGuid() }
        };
        foreach (var mutation in mutations)
        {
            await Assert.ThrowsAnyAsync<Exception>(() => fixture.Inputs.CommitBoundaryAsync(new(mutation,
                new(start.Turn with { Id = mutation.TurnId }, [], []))));
        }

        (await fixture.ScalarAsync("SELECT COUNT(*) FROM conversation_inputs WHERE status = 1;")).Should().Be(2L);
        (await fixture.Conversations.ListMessagesAsync(start.Turn.ConversationId)).Should().ContainSingle();
    }

    [Fact]
    public async Task Sqlite_constraints_reject_missing_turn_cross_conversation_and_consumed_without_mapping()
    {
        using var fixture = new ConversationPersistenceFixture();
        var start = await fixture.StartAsync();
        var other = await fixture.StartAsync();
        var batch = await fixture.SeedClaimAsync(start.Turn, Guid.NewGuid(), 1, "one");
        await Assert.ThrowsAsync<SqliteException>(() => fixture.ExecuteAsync("UPDATE conversation_inputs SET status = 2;"));
        await Assert.ThrowsAsync<SqliteException>(() => fixture.ExecuteAsync("UPDATE conversation_inputs SET target_turn_id = $turn;", ("$turn", other.Turn.Id.ToString("D"))));
        await Assert.ThrowsAsync<SqliteException>(() => fixture.ExecuteAsync("UPDATE messages SET turn_id = $turn;", ("$turn", Guid.NewGuid().ToString("D"))));
        await Assert.ThrowsAsync<SqliteException>(() => fixture.ExecuteAsync("UPDATE messages SET conversation_id = $id WHERE turn_id = $turn;",
            ("$id", other.Turn.ConversationId.ToString("D")), ("$turn", start.Turn.Id.ToString("D"))));
        await fixture.Inputs.CommitBoundaryAsync(new(batch, new(start.Turn, [], [])));
        await Assert.ThrowsAsync<SqliteException>(() => fixture.ExecuteAsync("UPDATE conversation_inputs SET message_id = $id;", ("$id", other.Messages[0].Id.ToString("D"))));
        await fixture.Conversations.DeleteConversationAsync(start.Turn.ConversationId);
        (await fixture.ScalarAsync("SELECT COUNT(*) FROM conversation_inputs;")).Should().Be(0L);
        (await fixture.ScalarAsync("SELECT COUNT(*) FROM conversation_input_state;")).Should().Be(0L);
    }

    [Fact]
    public async Task Sqlite_unique_input_order_request_ids_and_message_sequences_are_enforced()
    {
        using var fixture = new ConversationPersistenceFixture();
        var start = await fixture.StartAsync();
        await fixture.SeedClaimAsync(start.Turn, Guid.NewGuid(), 1, "input");
        const string clone = """
            INSERT INTO conversation_inputs(id, conversation_id, client_request_id, sequence, kind, target_turn_id,
                payload_json, status, revision, claim_id, claim_owner_run_id, created_at_utc, updated_at_utc)
            SELECT $id, conversation_id, $request, $sequence, kind, target_turn_id, payload_json, status, revision,
                claim_id, claim_owner_run_id, created_at_utc, updated_at_utc FROM conversation_inputs LIMIT 1;
            """;
        await Assert.ThrowsAsync<SqliteException>(() => fixture.ExecuteAsync(clone,
            ("$id", Guid.NewGuid().ToString("D")), ("$request", Guid.NewGuid().ToString("D")), ("$sequence", 1L)));
        var request = await fixture.ScalarAsync("SELECT client_request_id FROM conversation_inputs LIMIT 1;")
            ?? throw new InvalidOperationException("Missing request id.");
        await Assert.ThrowsAsync<SqliteException>(() => fixture.ExecuteAsync(clone,
            ("$id", Guid.NewGuid().ToString("D")), ("$request", request), ("$sequence", 2L)));
        await Assert.ThrowsAsync<SqliteException>(() => fixture.ExecuteAsync("""
            INSERT INTO messages(id, conversation_id, turn_id, sequence, role, markdown_content, status, created_at_utc, updated_at_utc)
            SELECT $id, conversation_id, turn_id, sequence, role, markdown_content, status, created_at_utc, updated_at_utc FROM messages LIMIT 1;
            """, ("$id", Guid.NewGuid().ToString("D"))));
        await Assert.ThrowsAsync<SqliteException>(() => fixture.ExecuteAsync("UPDATE messages SET turn_id = NULL;"));
        await Assert.ThrowsAsync<SqliteException>(() => fixture.ExecuteAsync("UPDATE messages SET sequence = NULL;"));
        await Assert.ThrowsAsync<SqliteException>(() => fixture.ExecuteAsync("UPDATE messages SET id = turn_id;"));
    }

    private static ToolExecutionRecord Tool(MessageRecord message, string correlation)
        => new(Guid.NewGuid(), message.ConversationId, "read_file", "{}", ToolExecutionStatus.Completed,
            "read", correlation, 1, message.CreatedAtUtc, message.UpdatedAtUtc, MessageId: message.Id, ResultContent: "content");
}
