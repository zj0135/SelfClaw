using System.Text.Json;
using FluentAssertions;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Infrastructure.Data.Sqlite;
using SelfClaw.Infrastructure.Data.Sqlite.Repositories;
using SelfClaw.Infrastructure.Options;

namespace SelfClaw.Tests.TestDoubles;

internal sealed class ConversationPersistenceFixture : IDisposable
{
    public ConversationPersistenceFixture(Func<string, CancellationToken, Task>? boundaryBeforeCommit = null,
        Func<string, CancellationToken, Task>? startBeforeCommit = null)
    {
        RootPath = Path.Combine(Path.GetTempPath(), "SelfClawTests", Guid.NewGuid().ToString("N"));
        Paths = StoragePathDefaults.Create(RootPath, Path.Combine(RootPath, "p2.db"), Path.Combine(RootPath, "secrets"));
        Database = new SqliteDatabase(Paths);
        Conversations = new SqliteConversationRepository(Database);
        Turns = startBeforeCommit is null
            ? new SqliteConversationTurnRepository(Database)
            : new SqliteConversationTurnRepository(Database, startBeforeCommit);
        Inputs = boundaryBeforeCommit is null
            ? new SqliteConversationInputRepository(Database)
            : new SqliteConversationInputRepository(Database, boundaryBeforeCommit);
    }

    internal string RootPath { get; }
    internal StoragePaths Paths { get; }
    internal SqliteDatabase Database { get; }
    internal SqliteConversationRepository Conversations { get; }
    internal SqliteConversationTurnRepository Turns { get; }
    internal SqliteConversationInputRepository Inputs { get; }

    internal async Task<ConversationTurnCommit> StartAsync(string prompt = "initial", AgentExecutionMode mode = AgentExecutionMode.Direct)
    {
        var now = DateTimeOffset.UtcNow;
        var conversation = new ConversationRecord(Guid.NewGuid(), "P2", null, ConversationMode.Programming,
            ToolPermissionMode.RequireApproval, "build", now, now);
        var turn = new ConversationTurnRecord(Guid.NewGuid(), conversation.Id, mode, DirectTurnOrigin.Interactive,
            ConversationTurnStatus.Running, now);
        return await Turns.StartTurnAsync(new ConversationTurnStart(conversation, turn, prompt, Guid.NewGuid()));
    }

    internal async Task<MessageRecord> AssistantAsync(ConversationTurnRecord turn, string text = "answer", MessageStatus status = MessageStatus.Streaming)
    {
        var id = Guid.NewGuid();
        var sequence = await Turns.ReserveMessageSequenceAsync(turn.ConversationId);
        return new MessageRecord(id, turn.ConversationId, turn.Id, sequence, MessageRole.Assistant, text, status,
            turn.StartedAtUtc, turn.StartedAtUtc, Segments: [new MessageSegmentRecord(id, 0, MessageSegmentKind.Text, text, null)]);
    }

    internal async Task<ConversationRecord> CreateConversationAsync(string title = "Conversation")
    {
        var now = DateTimeOffset.UtcNow;
        var conversation = new ConversationRecord(Guid.NewGuid(), title, null, ConversationMode.Programming,
            ToolPermissionMode.RequireApproval, "build", now, now);
        return await Conversations.UpsertConversationAsync(conversation);
    }

    internal ConversationInputExecutionSnapshot Snapshot(string agentId = "build", long revision = 1,
        Guid? modelProfileId = null, Guid? workspaceRootId = null, string? workspaceRootPath = null)
        => new(agentId, revision, AgentExecutionMode.Direct, modelProfileId ?? Guid.NewGuid(),
            workspaceRootId, workspaceRootPath, ToolPermissionMode.RequireApproval);

    internal async Task<ConversationInputRecord> AcceptFollowUpAsync(Guid conversationId, string prompt,
        string? requestId = null, ConversationInputExecutionSnapshot? snapshot = null, bool queueEnabled = true)
    {
        var result = await Inputs.AcceptAsync(new ConversationInputAcceptRequest(conversationId,
            requestId ?? Guid.NewGuid().ToString("N"), ConversationInputKind.FollowUp, null, prompt,
            snapshot ?? Snapshot(), null, queueEnabled));
        result.Status.Should().Be(ConversationInputAcceptStatus.Accepted);
        return result.Input!;
    }

    internal async Task<ConversationInputRecord> ClaimNextAsync(Guid conversationId, Guid ownerRunId, Guid claimId)
        => await Inputs.TryClaimNextFollowUpAsync(conversationId, ownerRunId, claimId)
           ?? throw new InvalidOperationException("No pending follow-up could be claimed.");

    internal async Task<ConversationTurnCommit> StartClaimedAsync(ConversationRecord conversation,
        ConversationInputRecord input, Guid claimId, Guid ownerRunId)
    {
        var turnId = Guid.NewGuid();
        var turn = new ConversationTurnRecord(turnId, conversation.Id, AgentExecutionMode.Direct,
            DirectTurnOrigin.Interactive, ConversationTurnStatus.Running, DateTimeOffset.UtcNow);
        var claim = new ConversationInputClaim(input.Id, conversation.Id, input.Revision, claimId, ownerRunId,
            ConversationInputMessageId.Compute(claimId, input.Id));
        return await Turns.StartTurnAsync(new ConversationTurnStart(conversation, turn, input.Prompt,
            claim.MessageId, InputClaim: claim));
    }

    internal async Task<ConversationInputBatch> SeedClaimAsync(
        ConversationTurnRecord turn, Guid ownerRunId, long firstSequence = 1, params string[] prompts)
    {
        var claimId = Guid.NewGuid();
        for (var index = 0; index < prompts.Length; index++)
        {
            await ExecuteAsync("""
                INSERT INTO conversation_inputs(id, conversation_id, client_request_id, sequence, kind, target_turn_id,
                    payload_json, status, revision, claim_id, claim_owner_run_id, created_at_utc, updated_at_utc)
                VALUES($id, $conversation, $request, $sequence, 1, $turn, $payload, 1, 1, $claim, $owner, $at, $at);
                """, ("$id", Guid.NewGuid().ToString("D")), ("$conversation", turn.ConversationId.ToString("D")),
                ("$request", Guid.NewGuid().ToString("D")), ("$sequence", firstSequence + index),
                ("$turn", turn.Id.ToString("D")), ("$payload", JsonSerializer.Serialize(new { version = 1, prompt = prompts[index] })),
                ("$claim", claimId.ToString("D")), ("$owner", ownerRunId.ToString("D")), ("$at", DateTimeOffset.UtcNow.ToString("O")));
        }

        return await Inputs.ReadClaimedBatchAsync(turn.ConversationId, turn.Id, ownerRunId)
            ?? throw new InvalidOperationException("The seeded claim was not returned.");
    }

    internal async Task ExecuteAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = await Database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        }

        await command.ExecuteNonQueryAsync();
    }

    internal async Task<object?> ScalarAsync(string sql)
    {
        await using var connection = await Database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }

    internal async Task<object?> ScalarAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = await Database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        }

        return await command.ExecuteScalarAsync();
    }

    public void Dispose()
    {
        SqliteTestPools.ClearFor(Paths.DatabasePath);
        for (var attempt = 0; attempt < 10 && Directory.Exists(RootPath); attempt++)
        {
            try
            {
                Directory.Delete(RootPath, recursive: true);
            }
            catch (IOException)
            {
                Thread.Sleep(25);
                SqliteTestPools.ClearFor(Paths.DatabasePath);
            }
        }
    }
}
