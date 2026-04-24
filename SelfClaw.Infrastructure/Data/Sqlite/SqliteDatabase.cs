using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SelfClaw.Infrastructure.Options;

namespace SelfClaw.Infrastructure.Data.Sqlite;

public sealed class SqliteDatabase
{
    private const int CurrentSchemaVersion = 13;
    private readonly StoragePaths _storagePaths;
    private readonly SemaphoreSlim _initializationGate = new(1, 1);
    private readonly ILogger<SqliteDatabase> _logger;
    private bool _initialized;

    public SqliteDatabase(StoragePaths storagePaths, ILogger<SqliteDatabase>? logger = null)
    {
        _storagePaths = storagePaths;
        _logger = logger ?? NullLogger<SqliteDatabase>.Instance;
    }

    public async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await EnsureInitializedAsync(cancellationToken);

            var connection = new SqliteConnection($"Data Source={_storagePaths.DatabasePath}");
            await connection.OpenAsync(cancellationToken);

            await using var pragma = connection.CreateCommand();
            pragma.CommandText = "PRAGMA foreign_keys = ON;";
            await pragma.ExecuteNonQueryAsync(cancellationToken);

            return connection;
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Opening SQLite connection was canceled. DatabasePath={DatabasePath}", _storagePaths.DatabasePath);
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed to open SQLite connection. DatabasePath={DatabasePath}", _storagePaths.DatabasePath);
            throw;
        }
    }

    public async Task EnsureInitializedAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized)
        {
            return;
        }

        await _initializationGate.WaitAsync(cancellationToken);
        try
        {
            if (_initialized)
            {
                return;
            }

            Directory.CreateDirectory(_storagePaths.AppDataDirectory);

            await using var connection = new SqliteConnection($"Data Source={_storagePaths.DatabasePath}");
            await connection.OpenAsync(cancellationToken);

            await ExecuteAsync(connection, @"
CREATE TABLE IF NOT EXISTS schema_versions (
    version INTEGER NOT NULL PRIMARY KEY,
    applied_at_utc TEXT NOT NULL
);", cancellationToken);

            await ExecuteAsync(connection, @"
CREATE TABLE IF NOT EXISTS profiles (
    id TEXT NOT NULL PRIMARY KEY,
    name TEXT NOT NULL,
    endpoint TEXT NOT NULL,
    model TEXT NOT NULL,
    temperature_enabled INTEGER NOT NULL DEFAULT 0,
    temperature REAL NOT NULL DEFAULT 0.7,
    top_p_enabled INTEGER NOT NULL DEFAULT 0,
    top_p REAL NOT NULL DEFAULT 0.7,
    api_style INTEGER NOT NULL,
    secret_ref TEXT NOT NULL,
    created_at_utc TEXT NOT NULL,
    updated_at_utc TEXT NOT NULL
);", cancellationToken);

            await EnsureColumnExistsAsync(
                connection,
                "profiles",
                "temperature_enabled",
                "ALTER TABLE profiles ADD COLUMN temperature_enabled INTEGER NOT NULL DEFAULT 0;",
                cancellationToken);

            await EnsureColumnExistsAsync(
                connection,
                "profiles",
                "temperature",
                "ALTER TABLE profiles ADD COLUMN temperature REAL NOT NULL DEFAULT 0.7;",
                cancellationToken);

            await EnsureColumnExistsAsync(
                connection,
                "profiles",
                "top_p",
                "ALTER TABLE profiles ADD COLUMN top_p REAL NOT NULL DEFAULT 0.7;",
                cancellationToken);

            await EnsureColumnExistsAsync(
                connection,
                "profiles",
                "top_p_enabled",
                "ALTER TABLE profiles ADD COLUMN top_p_enabled INTEGER NOT NULL DEFAULT 0;",
                cancellationToken);

            await ExecuteAsync(connection, @"
CREATE TABLE IF NOT EXISTS workspace_roots (
    id TEXT NOT NULL PRIMARY KEY,
    name TEXT NOT NULL,
    root_path TEXT NOT NULL,
    created_at_utc TEXT NOT NULL,
    updated_at_utc TEXT NOT NULL
);", cancellationToken);

            await ExecuteAsync(connection, @"
CREATE TABLE IF NOT EXISTS conversations (
    id TEXT NOT NULL PRIMARY KEY,
    title TEXT NOT NULL,
    profile_id TEXT NOT NULL,
    workspace_root_id TEXT NULL,
    mode INTEGER NOT NULL DEFAULT 0,
    tool_permission_mode INTEGER NOT NULL DEFAULT 0,
    team_max_rounds INTEGER NOT NULL DEFAULT 2,
    team_output_mode INTEGER NOT NULL DEFAULT 1,
    parent_conversation_id TEXT NULL,
    root_conversation_id TEXT NULL,
    bound_agent_id TEXT NULL,
    bound_agent_name TEXT NULL,
    bound_agent_role TEXT NULL,
    channel_kind TEXT NULL,
    channel_conversation_id TEXT NULL,
    channel_display_name TEXT NULL,
    created_at_utc TEXT NOT NULL,
    updated_at_utc TEXT NOT NULL,
    FOREIGN KEY(profile_id) REFERENCES profiles(id) ON DELETE RESTRICT,
    FOREIGN KEY(workspace_root_id) REFERENCES workspace_roots(id) ON DELETE SET NULL,
    FOREIGN KEY(parent_conversation_id) REFERENCES conversations(id) ON DELETE CASCADE,
    FOREIGN KEY(root_conversation_id) REFERENCES conversations(id) ON DELETE CASCADE
);", cancellationToken);

            await EnsureColumnExistsAsync(
                connection,
                "conversations",
                "mode",
                "ALTER TABLE conversations ADD COLUMN mode INTEGER NOT NULL DEFAULT 0;",
                cancellationToken);

            await EnsureColumnExistsAsync(
                connection,
                "conversations",
                "tool_permission_mode",
                "ALTER TABLE conversations ADD COLUMN tool_permission_mode INTEGER NOT NULL DEFAULT 0;",
                cancellationToken);

            await EnsureColumnExistsAsync(
                connection,
                "conversations",
                "team_max_rounds",
                "ALTER TABLE conversations ADD COLUMN team_max_rounds INTEGER NOT NULL DEFAULT 2;",
                cancellationToken);

            await EnsureColumnExistsAsync(
                connection,
                "conversations",
                "team_output_mode",
                "ALTER TABLE conversations ADD COLUMN team_output_mode INTEGER NOT NULL DEFAULT 1;",
                cancellationToken);

            await EnsureColumnExistsAsync(
                connection,
                "conversations",
                "parent_conversation_id",
                "ALTER TABLE conversations ADD COLUMN parent_conversation_id TEXT NULL;",
                cancellationToken);

            await EnsureColumnExistsAsync(
                connection,
                "conversations",
                "root_conversation_id",
                "ALTER TABLE conversations ADD COLUMN root_conversation_id TEXT NULL;",
                cancellationToken);

            await EnsureColumnExistsAsync(
                connection,
                "conversations",
                "bound_agent_id",
                "ALTER TABLE conversations ADD COLUMN bound_agent_id TEXT NULL;",
                cancellationToken);

            await EnsureColumnExistsAsync(
                connection,
                "conversations",
                "bound_agent_name",
                "ALTER TABLE conversations ADD COLUMN bound_agent_name TEXT NULL;",
                cancellationToken);

            await EnsureColumnExistsAsync(
                connection,
                "conversations",
                "bound_agent_role",
                "ALTER TABLE conversations ADD COLUMN bound_agent_role TEXT NULL;",
                cancellationToken);

            await EnsureColumnExistsAsync(
                connection,
                "conversations",
                "channel_kind",
                "ALTER TABLE conversations ADD COLUMN channel_kind TEXT NULL;",
                cancellationToken);

            await EnsureColumnExistsAsync(
                connection,
                "conversations",
                "channel_conversation_id",
                "ALTER TABLE conversations ADD COLUMN channel_conversation_id TEXT NULL;",
                cancellationToken);

            await EnsureColumnExistsAsync(
                connection,
                "conversations",
                "channel_display_name",
                "ALTER TABLE conversations ADD COLUMN channel_display_name TEXT NULL;",
                cancellationToken);

            await ExecuteAsync(connection, @"
CREATE TABLE IF NOT EXISTS messages (
    id TEXT NOT NULL PRIMARY KEY,
    conversation_id TEXT NOT NULL,
    role INTEGER NOT NULL,
    markdown_content TEXT NOT NULL,
    status INTEGER NOT NULL,
    created_at_utc TEXT NOT NULL,
    updated_at_utc TEXT NOT NULL,
    agent_id TEXT NULL,
    agent_name TEXT NULL,
    agent_role TEXT NULL,
    input_tokens INTEGER NULL,
    output_tokens INTEGER NULL,
    duration_ms REAL NULL,
    error_message TEXT NULL,
    FOREIGN KEY(conversation_id) REFERENCES conversations(id) ON DELETE CASCADE
);", cancellationToken);

            await EnsureColumnExistsAsync(
                connection,
                "messages",
                "agent_id",
                "ALTER TABLE messages ADD COLUMN agent_id TEXT NULL;",
                cancellationToken);

            await EnsureColumnExistsAsync(
                connection,
                "messages",
                "agent_name",
                "ALTER TABLE messages ADD COLUMN agent_name TEXT NULL;",
                cancellationToken);

            await EnsureColumnExistsAsync(
                connection,
                "messages",
                "agent_role",
                "ALTER TABLE messages ADD COLUMN agent_role TEXT NULL;",
                cancellationToken);

            await ExecuteAsync(connection, @"
CREATE TABLE IF NOT EXISTS message_attachments (
    id TEXT NOT NULL PRIMARY KEY,
    message_id TEXT NOT NULL,
    kind INTEGER NOT NULL,
    file_name TEXT NOT NULL,
    media_type TEXT NOT NULL,
    storage_path TEXT NOT NULL,
    byte_length INTEGER NOT NULL,
    created_at_utc TEXT NOT NULL,
    FOREIGN KEY(message_id) REFERENCES messages(id) ON DELETE CASCADE
);", cancellationToken);

            await ExecuteAsync(connection, @"
CREATE TABLE IF NOT EXISTS team_agents (
    id TEXT NOT NULL PRIMARY KEY,
    conversation_id TEXT NOT NULL,
    name TEXT NOT NULL,
    role TEXT NOT NULL,
    goal_prompt TEXT NOT NULL,
    status INTEGER NOT NULL,
    sort_order INTEGER NOT NULL,
    created_at_utc TEXT NOT NULL,
    updated_at_utc TEXT NOT NULL,
    FOREIGN KEY(conversation_id) REFERENCES conversations(id) ON DELETE CASCADE
);", cancellationToken);

            await ExecuteAsync(connection, @"
CREATE TABLE IF NOT EXISTS tool_runs (
    id TEXT NOT NULL PRIMARY KEY,
    conversation_id TEXT NOT NULL,
    tool_name TEXT NOT NULL,
    arguments_json TEXT NOT NULL,
    status INTEGER NOT NULL,
    result_summary TEXT NULL,
    result_content TEXT NULL,
    correlation_id TEXT NULL,
    duration_ms REAL NULL,
    created_at_utc TEXT NOT NULL,
    updated_at_utc TEXT NOT NULL,
    agent_id TEXT NULL,
    message_id TEXT NULL,
    after_segment_index INTEGER NULL,
    FOREIGN KEY(conversation_id) REFERENCES conversations(id) ON DELETE CASCADE
);", cancellationToken);

            await EnsureColumnExistsAsync(
                connection,
                "tool_runs",
                "result_content",
                "ALTER TABLE tool_runs ADD COLUMN result_content TEXT NULL;",
                cancellationToken);

            await EnsureColumnExistsAsync(
                connection,
                "tool_runs",
                "agent_id",
                "ALTER TABLE tool_runs ADD COLUMN agent_id TEXT NULL;",
                cancellationToken);

            await EnsureColumnExistsAsync(
                connection,
                "tool_runs",
                "message_id",
                "ALTER TABLE tool_runs ADD COLUMN message_id TEXT NULL;",
                cancellationToken);

            await EnsureColumnExistsAsync(
                connection,
                "tool_runs",
                "after_segment_index",
                "ALTER TABLE tool_runs ADD COLUMN after_segment_index INTEGER NULL;",
                cancellationToken);

            await ExecuteAsync(connection, "CREATE INDEX IF NOT EXISTS ix_conversations_updated ON conversations(updated_at_utc DESC);", cancellationToken);
            await ExecuteAsync(connection, "CREATE INDEX IF NOT EXISTS ix_messages_conversation_created ON messages(conversation_id, created_at_utc);", cancellationToken);
            await ExecuteAsync(connection, "CREATE INDEX IF NOT EXISTS ix_message_attachments_message ON message_attachments(message_id, created_at_utc);", cancellationToken);
            await ExecuteAsync(connection, "CREATE INDEX IF NOT EXISTS ix_team_agents_conversation_sort ON team_agents(conversation_id, sort_order, created_at_utc);", cancellationToken);
            await ExecuteAsync(connection, "CREATE INDEX IF NOT EXISTS ix_tool_runs_conversation_created ON tool_runs(conversation_id, created_at_utc);", cancellationToken);
            for (var version = 1; version <= CurrentSchemaVersion; version++)
            {
                await ExecuteAsync(
                    connection,
                    $"INSERT OR IGNORE INTO schema_versions(version, applied_at_utc) VALUES({version}, CURRENT_TIMESTAMP);",
                    cancellationToken);
            }

            _initialized = true;
            _logger.LogInformation(
                "SQLite database initialized. DatabasePath={DatabasePath}, SchemaVersion={SchemaVersion}",
                _storagePaths.DatabasePath,
                CurrentSchemaVersion);
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("SQLite initialization was canceled. DatabasePath={DatabasePath}", _storagePaths.DatabasePath);
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "Failed to initialize SQLite database. DatabasePath={DatabasePath}, SchemaVersion={SchemaVersion}",
                _storagePaths.DatabasePath,
                CurrentSchemaVersion);
            throw;
        }
        finally
        {
            _initializationGate.Release();
        }
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnsureColumnExistsAsync(
        SqliteConnection connection,
        string tableName,
        string columnName,
        string alterSql,
        CancellationToken cancellationToken)
    {
        var hasColumn = false;

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = $"PRAGMA table_info({tableName});";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
                {
                    hasColumn = true;
                    break;
                }
            }
        }

        if (!hasColumn)
        {
            await ExecuteAsync(connection, alterSql, cancellationToken);
        }
    }
}
