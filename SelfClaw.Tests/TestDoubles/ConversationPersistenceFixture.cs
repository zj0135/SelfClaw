using System.Text.Json;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Infrastructure.Data.Sqlite;
using SelfClaw.Infrastructure.Data.Sqlite.Repositories;
using SelfClaw.Infrastructure.Options;

namespace SelfClaw.Tests.TestDoubles;

internal sealed class ConversationPersistenceFixture : IDisposable
{
    public ConversationPersistenceFixture(Func<string, CancellationToken, Task>? boundaryBeforeCommit = null)
    {
        RootPath = Path.Combine(Path.GetTempPath(), "SelfClawTests", Guid.NewGuid().ToString("N"));
        Paths = StoragePathDefaults.Create(RootPath, Path.Combine(RootPath, "p2.db"), Path.Combine(RootPath, "secrets"));
        Database = new SqliteDatabase(Paths);
        Conversations = new SqliteConversationRepository(Database);
        Turns = new SqliteConversationTurnRepository(Database);
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

    public void Dispose()
    {
        SqliteTestPools.ClearFor(Paths.DatabasePath);
        if (Directory.Exists(RootPath))
        {
            Directory.Delete(RootPath, recursive: true);
        }
    }
}
