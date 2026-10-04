using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Core.Runtime.Agent;
using SelfClaw.Desktop.Services.ConversationInputs;
using SelfClaw.Desktop.Services.Runtime;
using SelfClaw.Infrastructure.Data.Sqlite;
using SelfClaw.Infrastructure.Extensions.Skills;
using SelfClaw.Infrastructure.Data.Sqlite.Repositories;
using SelfClaw.Infrastructure.Options;

namespace SelfClaw.Tests.TestDoubles;

internal sealed class ConversationTurnTestContext : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "SelfClawP2Tests", Guid.NewGuid().ToString("N"));
    private ConversationRuntimeState? _state;
    private long _inputSequence;

    internal ConversationTurnTestContext()
    {
        Database = new SqliteDatabase(StoragePathDefaults.Create(_root, Path.Combine(_root, "turns.db"), Path.Combine(_root, "secrets")));
        Conversations = new SqliteConversationRepository(Database);
        Turns = new SqliteConversationTurnRepository(Database);
        Inputs = new SqliteConversationInputRepository(Database, new SkillTokenParser());
        var now = DateTimeOffset.UtcNow;
        Conversation = new ConversationRecord(Guid.NewGuid(), "P2 fixture", null, ToolPermissionMode.RequireApproval, "build", now, now);
        Record = new ConversationTurnRecord(Guid.NewGuid(), Conversation.Id, AgentExecutionMode.Direct,
            DirectTurnOrigin.Interactive, ConversationTurnStatus.Running, now);
        Agent = new AgentRuntimeDefinition("build", "Builder", "fixture", AgentExecutionMode.Direct,
            AgentRuntimeDefinition.SystemToolPolicy, [], [], [], [], string.Empty);
        InputSession = new DirectTurnInputSession(Conversation.Id, Record.Id, RunId, Inputs, Inputs);
        Turn = new AgentTurnState(Record, Agent, InputSession);
        Recorder = CreateRecorder();
        Finalizer = new DesktopTurnFinalizer(Turns, NullLogger<DesktopTurnFinalizer>.Instance);
    }

    internal SqliteDatabase Database { get; }
    internal SqliteConversationRepository Conversations { get; }
    internal SqliteConversationTurnRepository Turns { get; }
    internal SqliteConversationInputRepository Inputs { get; }
    internal ConversationRecord Conversation { get; }
    internal ConversationTurnRecord Record { get; }
    internal AgentRuntimeDefinition Agent { get; }
    internal Guid RunId { get; } = Guid.NewGuid();
    internal DirectTurnInputSession InputSession { get; }
    internal AgentTurnState Turn { get; }
    internal ConversationTurnRecorder Recorder { get; }
    internal DesktopTurnFinalizer Finalizer { get; }
    internal ConversationRuntimeState State => _state ?? throw new InvalidOperationException("Start the fixture first.");

    internal async Task StartAsync(string prompt = "U0")
    {
        var start = await Turns.StartTurnAsync(new ConversationTurnStart(Conversation, Record, prompt, Guid.NewGuid()));
        _state = new ConversationRuntimeState(Conversation, [start.Turn], start.Messages, []);
        Recorder.BeginTurn(State, Turn);
    }

    internal ConversationTurnRecorder CreateRecorder(IConversationTurnRepository? turns = null, IConversationInputRepository? inputs = null)
        => new(Conversations, turns ?? Turns, inputs ?? Inputs, NullLogger<ConversationTurnRecorder>.Instance);

    internal Task ApplyAsync(AgentStreamEvent streamEvent)
        => Recorder.ApplyEventAsync(State, Turn, streamEvent, Finalizer, CancellationToken.None);

    internal async Task<ConversationInputBatch> SeedClaimAsync(params string[] prompts)
    {
        await SeedPendingSteerAsync(prompts);
        return await InputSession.ReadBoundaryAsync()
            ?? throw new InvalidOperationException("The pending fixture batch was not claimable.");
    }

    /// <summary>Inserts Pending steers targeted at this turn without claiming them.</summary>
    internal async Task SeedPendingSteerAsync(params string[] prompts)
    {
        var snapshot = JsonSerializer.Serialize(new ConversationInputExecutionSnapshot("build", 1,
            AgentExecutionMode.Direct, Guid.NewGuid(), null, null, ToolPermissionMode.RequireApproval),
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        await using var connection = await Database.OpenConnectionAsync();
        await using var transaction = connection.BeginTransaction();
        foreach (var prompt in prompts)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO conversation_inputs
                    (id, conversation_id, client_request_id, sequence, kind, target_turn_id,
                     payload_json, execution_snapshot_json, status, revision, claim_id, claim_owner_run_id,
                     created_at_utc, updated_at_utc)
                VALUES ($id, $conversation, $request, $sequence, 1, $turn, $payload, $snapshot, 0, 1, NULL, NULL, $now, $now);
                """;
            command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString());
            command.Parameters.AddWithValue("$conversation", Conversation.Id.ToString());
            command.Parameters.AddWithValue("$request", Guid.NewGuid().ToString());
            command.Parameters.AddWithValue("$sequence", ++_inputSequence);
            command.Parameters.AddWithValue("$turn", Record.Id.ToString());
            command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(new { version = 1, prompt }));
            command.Parameters.AddWithValue("$snapshot", snapshot);
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            await command.ExecuteNonQueryAsync();
        }
        await transaction.CommitAsync();
    }

    internal async Task ExecuteSqlAsync(string sql)
    {
        await using var connection = await Database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
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
        InputSession.Close();
        if (!Directory.Exists(_root)) return;
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }
}
