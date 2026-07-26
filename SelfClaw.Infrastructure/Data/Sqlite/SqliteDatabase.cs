using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SelfClaw.Infrastructure.Options;

namespace SelfClaw.Infrastructure.Data.Sqlite;

public sealed class SqliteDatabase
{
    private const int CurrentSchemaVersion = 22;
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
CREATE TABLE IF NOT EXISTS ai_provider_connections (
    id TEXT NOT NULL PRIMARY KEY,
    catalog_id TEXT NOT NULL DEFAULT 'custom',
    name TEXT NOT NULL,
    provider_kind INTEGER NOT NULL,
    endpoint TEXT NOT NULL,
    auth_kind INTEGER NOT NULL,
    credential_refs_json TEXT NOT NULL DEFAULT '{}',
    connection_options_json TEXT NOT NULL DEFAULT '{}',
    is_enabled INTEGER NOT NULL DEFAULT 1,
    created_at_utc TEXT NOT NULL,
    updated_at_utc TEXT NOT NULL
);", cancellationToken);

            await EnsureColumnExistsAsync(
                connection,
                "ai_provider_connections",
                "catalog_id",
                "ALTER TABLE ai_provider_connections ADD COLUMN catalog_id TEXT NOT NULL DEFAULT 'custom';",
                cancellationToken);

            await ExecuteAsync(connection, @"
CREATE TABLE IF NOT EXISTS ai_model_profiles (
    id TEXT NOT NULL PRIMARY KEY,
    provider_connection_id TEXT NOT NULL,
    name TEXT NOT NULL,
    api_format INTEGER NOT NULL,
    model TEXT NOT NULL,
    temperature_enabled INTEGER NOT NULL DEFAULT 0,
    temperature REAL NOT NULL DEFAULT 0.7,
    top_p_enabled INTEGER NOT NULL DEFAULT 0,
    top_p REAL NOT NULL DEFAULT 0.7,
    model_options_json TEXT NOT NULL DEFAULT '{}',
    is_enabled INTEGER NOT NULL DEFAULT 1,
    created_at_utc TEXT NOT NULL,
    updated_at_utc TEXT NOT NULL,
    FOREIGN KEY(provider_connection_id) REFERENCES ai_provider_connections(id) ON DELETE CASCADE
);", cancellationToken);

            await ExecuteAsync(connection, @"
CREATE TABLE IF NOT EXISTS ai_model_profile_selections (
    scope TEXT NOT NULL PRIMARY KEY,
    model_profile_id TEXT NOT NULL,
    updated_at_utc TEXT NOT NULL,
    FOREIGN KEY(model_profile_id) REFERENCES ai_model_profiles(id) ON DELETE CASCADE
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
    workspace_root_id TEXT NULL,
    mode INTEGER NOT NULL DEFAULT 0,
    tool_permission_mode INTEGER NOT NULL DEFAULT 0,
    agent_id TEXT NOT NULL DEFAULT 'build',
    channel_kind TEXT NULL,
    channel_conversation_id TEXT NULL,
    channel_display_name TEXT NULL,
    created_at_utc TEXT NOT NULL,
    updated_at_utc TEXT NOT NULL,
    FOREIGN KEY(workspace_root_id) REFERENCES workspace_roots(id) ON DELETE SET NULL
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
                "agent_id",
                "ALTER TABLE conversations ADD COLUMN agent_id TEXT NOT NULL DEFAULT 'build';",
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

            // Schema v21: provider/model selection moved to ai_model_profile_selections and the
            // per-turn request. Rebuild old conversation tables to remove their profiles foreign key
            // while preserving every conversation row and all dependent message/tool/session rows.
            await EnsureConversationsWithoutProfileIdAsync(connection, cancellationToken);
            await ExecuteAsync(connection, "DROP TABLE IF EXISTS profiles;", cancellationToken);

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
    source_kind INTEGER NULL,
    source_id TEXT NULL,
    display_name TEXT NULL,
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

            await EnsureColumnExistsAsync(
                connection,
                "tool_runs",
                "source_kind",
                "ALTER TABLE tool_runs ADD COLUMN source_kind INTEGER NULL;",
                cancellationToken);

            await EnsureColumnExistsAsync(
                connection,
                "tool_runs",
                "source_id",
                "ALTER TABLE tool_runs ADD COLUMN source_id TEXT NULL;",
                cancellationToken);

            await EnsureColumnExistsAsync(
                connection,
                "tool_runs",
                "display_name",
                "ALTER TABLE tool_runs ADD COLUMN display_name TEXT NULL;",
                cancellationToken);

            await ExecuteAsync(connection, @"
CREATE TABLE IF NOT EXISTS extension_packages (
    kind INTEGER NOT NULL,
    id TEXT NOT NULL,
    display_name TEXT NOT NULL,
    version TEXT NOT NULL,
    description TEXT NOT NULL,
    install_path TEXT NOT NULL,
    content_hash TEXT NOT NULL,
    manifest_json TEXT NOT NULL,
    source_plugin_id TEXT NULL,
    is_enabled INTEGER NOT NULL DEFAULT 0,
    acknowledged_permissions_json TEXT NULL,
    acknowledged_at_utc TEXT NULL,
    installed_at_utc TEXT NOT NULL,
    updated_at_utc TEXT NOT NULL,
    PRIMARY KEY(kind, id)
);", cancellationToken);

            await ExecuteAsync(connection, @"
CREATE TABLE IF NOT EXISTS mcp_server_configs (
    id TEXT NOT NULL PRIMARY KEY,
    display_name TEXT NOT NULL,
    transport INTEGER NOT NULL,
    settings_json TEXT NOT NULL,
    credential_refs_json TEXT NOT NULL,
    source_plugin_id TEXT NULL,
    is_enabled INTEGER NOT NULL DEFAULT 0,
    config_revision INTEGER NOT NULL DEFAULT 1,
    discovered_tools_json TEXT NOT NULL DEFAULT '[]',
    last_status INTEGER NOT NULL DEFAULT 0,
    last_error TEXT NULL,
    last_checked_at_utc TEXT NULL,
    created_at_utc TEXT NOT NULL,
    updated_at_utc TEXT NOT NULL
);", cancellationToken);

            await ExecuteAsync(connection, @"
CREATE TABLE IF NOT EXISTS cli_agent_sessions (
    conversation_id TEXT NOT NULL,
    agent_kind INTEGER NOT NULL,
    session_id TEXT NOT NULL,
    created_at_utc TEXT NOT NULL,
    updated_at_utc TEXT NOT NULL,
    PRIMARY KEY(conversation_id, agent_kind),
    FOREIGN KEY(conversation_id) REFERENCES conversations(id) ON DELETE CASCADE
);", cancellationToken);

            await ExecuteAsync(connection, "CREATE INDEX IF NOT EXISTS ix_conversations_updated ON conversations(updated_at_utc DESC);", cancellationToken);
            await ExecuteAsync(connection, "CREATE INDEX IF NOT EXISTS ix_messages_conversation_created ON messages(conversation_id, created_at_utc);", cancellationToken);
            await ExecuteAsync(connection, "CREATE INDEX IF NOT EXISTS ix_message_attachments_message ON message_attachments(message_id, created_at_utc);", cancellationToken);
            await ExecuteAsync(connection, "CREATE INDEX IF NOT EXISTS ix_tool_runs_conversation_created ON tool_runs(conversation_id, created_at_utc);", cancellationToken);
            await ExecuteAsync(connection, "CREATE INDEX IF NOT EXISTS ix_ai_provider_connections_kind ON ai_provider_connections(provider_kind);", cancellationToken);
            await ExecuteAsync(connection, "CREATE INDEX IF NOT EXISTS ix_ai_model_profiles_connection ON ai_model_profiles(provider_connection_id);", cancellationToken);
            await ExecuteAsync(connection, "CREATE INDEX IF NOT EXISTS ix_ai_model_profiles_updated ON ai_model_profiles(updated_at_utc DESC);", cancellationToken);
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

    /// <summary>Rebuilds legacy conversation tables to remove the obsolete profile_id column.</summary>
    private static async Task EnsureConversationsWithoutProfileIdAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var hasProfileId = false;
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA table_info(conversations);";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                // PRAGMA table_info columns: cid, name, type, notnull, dflt_value, pk.
                if (string.Equals(reader.GetString(1), "profile_id", StringComparison.OrdinalIgnoreCase))
                {
                    hasProfileId = true;
                    break;
                }
            }
        }

        if (!hasProfileId)
        {
            return;
        }

        // Foreign keys must be off while the table is swapped, and PRAGMA foreign_keys is a
        // no-op inside a transaction, so toggle it around the transaction boundaries.
        await ExecuteAsync(connection, "PRAGMA foreign_keys = OFF;", cancellationToken);
        // Conversations are user data: run the swap as one atomic unit so a crash mid-rebuild
        // can never leave the database without a populated conversations table.
        await ExecuteAsync(connection, "BEGIN IMMEDIATE;", cancellationToken);
        try
        {
            await ExecuteAsync(connection, "DROP TABLE IF EXISTS conversations_new;", cancellationToken);
            await ExecuteAsync(connection, @"
CREATE TABLE conversations_new (
    id TEXT NOT NULL PRIMARY KEY,
    title TEXT NOT NULL,
    workspace_root_id TEXT NULL,
    mode INTEGER NOT NULL DEFAULT 0,
    tool_permission_mode INTEGER NOT NULL DEFAULT 0,
    agent_id TEXT NOT NULL DEFAULT 'build',
    channel_kind TEXT NULL,
    channel_conversation_id TEXT NULL,
    channel_display_name TEXT NULL,
    created_at_utc TEXT NOT NULL,
    updated_at_utc TEXT NOT NULL,
    FOREIGN KEY(workspace_root_id) REFERENCES workspace_roots(id) ON DELETE SET NULL
);", cancellationToken);
            await ExecuteAsync(connection, @"
INSERT INTO conversations_new(
    id, title, workspace_root_id, mode, tool_permission_mode, agent_id,
    channel_kind, channel_conversation_id, channel_display_name, created_at_utc, updated_at_utc)
SELECT
    id, title, workspace_root_id, mode, tool_permission_mode, agent_id,
    channel_kind, channel_conversation_id, channel_display_name, created_at_utc, updated_at_utc
FROM conversations;", cancellationToken);
            await ExecuteAsync(connection, "DROP TABLE conversations;", cancellationToken);
            await ExecuteAsync(connection, "ALTER TABLE conversations_new RENAME TO conversations;", cancellationToken);
            await ExecuteAsync(connection, "COMMIT;", cancellationToken);
        }
        catch
        {
            try
            {
                await ExecuteAsync(connection, "ROLLBACK;", CancellationToken.None);
            }
            catch
            {
                // Initialization is already failing; surface the original error.
            }

            throw;
        }

        await ExecuteAsync(connection, "PRAGMA foreign_keys = ON;", cancellationToken);
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
