using Microsoft.Data.Sqlite;
using SelfClaw.Infrastructure.Options;

namespace SelfClaw.Infrastructure.Data;

public sealed class SqliteDatabase
{
    private readonly StoragePaths _storagePaths;
    private readonly SemaphoreSlim _initializationGate = new(1, 1);
    private bool _initialized;

    public SqliteDatabase(StoragePaths storagePaths)
    {
        _storagePaths = storagePaths;
    }

    public async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);

        var connection = new SqliteConnection($"Data Source={_storagePaths.DatabasePath}");
        await connection.OpenAsync(cancellationToken);

        await using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys = ON;";
        await pragma.ExecuteNonQueryAsync(cancellationToken);

        return connection;
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
    api_style INTEGER NOT NULL,
    secret_ref TEXT NOT NULL,
    created_at_utc TEXT NOT NULL,
    updated_at_utc TEXT NOT NULL
);", cancellationToken);

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
    tool_permission_mode INTEGER NOT NULL DEFAULT 0,
    created_at_utc TEXT NOT NULL,
    updated_at_utc TEXT NOT NULL,
    FOREIGN KEY(profile_id) REFERENCES profiles(id) ON DELETE RESTRICT,
    FOREIGN KEY(workspace_root_id) REFERENCES workspace_roots(id) ON DELETE SET NULL
);", cancellationToken);

            await EnsureColumnExistsAsync(
                connection,
                "conversations",
                "tool_permission_mode",
                "ALTER TABLE conversations ADD COLUMN tool_permission_mode INTEGER NOT NULL DEFAULT 0;",
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
    input_tokens INTEGER NULL,
    output_tokens INTEGER NULL,
    duration_ms REAL NULL,
    error_message TEXT NULL,
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
    correlation_id TEXT NULL,
    duration_ms REAL NULL,
    created_at_utc TEXT NOT NULL,
    updated_at_utc TEXT NOT NULL,
    FOREIGN KEY(conversation_id) REFERENCES conversations(id) ON DELETE CASCADE
);", cancellationToken);

            await ExecuteAsync(connection, "CREATE INDEX IF NOT EXISTS ix_conversations_updated ON conversations(updated_at_utc DESC);", cancellationToken);
            await ExecuteAsync(connection, "CREATE INDEX IF NOT EXISTS ix_messages_conversation_created ON messages(conversation_id, created_at_utc);", cancellationToken);
            await ExecuteAsync(connection, "CREATE INDEX IF NOT EXISTS ix_tool_runs_conversation_created ON tool_runs(conversation_id, created_at_utc);", cancellationToken);
            await ExecuteAsync(connection, "INSERT OR IGNORE INTO schema_versions(version, applied_at_utc) VALUES(1, CURRENT_TIMESTAMP);", cancellationToken);
            await ExecuteAsync(connection, "INSERT OR IGNORE INTO schema_versions(version, applied_at_utc) VALUES(2, CURRENT_TIMESTAMP);", cancellationToken);

            _initialized = true;
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
