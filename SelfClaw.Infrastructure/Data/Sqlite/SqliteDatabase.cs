using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SelfClaw.Infrastructure.Options;

namespace SelfClaw.Infrastructure.Data.Sqlite;

public sealed class SqliteDatabase
{
    private const int CurrentSchemaVersion = 30;
    private readonly StoragePaths _storagePaths;
    private readonly SemaphoreSlim _initializationGate = new(1, 1);
    private readonly ILogger<SqliteDatabase> _logger;
    private readonly Func<string, CancellationToken, Task>? _beforeCommit;
    private bool _initialized;

    public SqliteDatabase(StoragePaths storagePaths, ILogger<SqliteDatabase>? logger = null)
    {
        _storagePaths = storagePaths;
        _logger = logger ?? NullLogger<SqliteDatabase>.Instance;
    }

    internal SqliteDatabase(StoragePaths storagePaths, ILogger<SqliteDatabase>? logger, Func<string, CancellationToken, Task> beforeCommit)
        : this(storagePaths, logger)
    {
        _beforeCommit = beforeCommit;
    }

    public async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        var connection = CreateConnection(SqliteOpenMode.ReadWrite);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task EnsureInitializedAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized)
        {
            return;
        }

        await _initializationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized)
            {
                return;
            }

            // Inspection is genuinely read-only: rejecting a user's old database must not create
            // tables, journal files, version markers, or perform a compatibility repair.
            if (!await IsExistingTargetAsync(cancellationToken).ConfigureAwait(false))
            {
                await CreateTargetAsync(cancellationToken).ConfigureAwait(false);
            }

            _initialized = true;
            _logger.LogInformation("SQLite schema {SchemaVersion} opened. DatabasePath={DatabasePath}",
                CurrentSchemaVersion, _storagePaths.DatabasePath);
        }
        finally
        {
            _initializationGate.Release();
        }
    }

    private async Task<bool> IsExistingTargetAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_storagePaths.DatabasePath) || new FileInfo(_storagePaths.DatabasePath).Length == 0)
        {
            return false;
        }

        await using var connection = CreateConnection(SqliteOpenMode.ReadOnly);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await InspectSchemaAsync(connection, null, cancellationToken).ConfigureAwait(false);
    }

    private async Task CreateTargetAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_storagePaths.AppDataDirectory);
        await using var connection = CreateConnection(SqliteOpenMode.ReadWriteCreate);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = connection.BeginTransaction(deferred: false);
        // Recheck after acquiring the write lock in case another initializer won the race.
        if (!await InspectSchemaAsync(connection, transaction, cancellationToken).ConfigureAwait(false))
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = SqliteSchema.CreateSql;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            command.CommandText = "INSERT INTO schema_versions(version, applied_at_utc) VALUES($version, $at);";
            command.Parameters.AddWithValue("$version", CurrentSchemaVersion);
            command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            if (_beforeCommit is not null)
            {
                await _beforeCommit("schema-before-commit", cancellationToken).ConfigureAwait(false);
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private SqliteConnection CreateConnection(SqliteOpenMode mode)
        => new(new SqliteConnectionStringBuilder
        {
            DataSource = _storagePaths.DatabasePath,
            Mode = mode,
            ForeignKeys = true,
            Pooling = mode == SqliteOpenMode.ReadWrite
        }.ToString());

    private static async Task<bool> InspectSchemaAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT name FROM sqlite_schema WHERE name NOT LIKE 'sqlite_%';";
        var objects = new HashSet<string>(StringComparer.Ordinal);
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                objects.Add(reader.GetString(0));
            }
        }

        if (objects.Count == 0)
        {
            return false;
        }

        if (!objects.Contains("schema_versions"))
        {
            throw UnsupportedSchema("unversioned");
        }

        command.CommandText = "SELECT version FROM schema_versions;";
        var versions = new List<long>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                versions.Add(reader.GetInt64(0));
            }
        }

        if (versions.Count != 1 || versions[0] != CurrentSchemaVersion)
        {
            throw UnsupportedSchema(string.Join(", ", versions));
        }

        foreach (var table in SqliteSchema.RequiredTables)
        {
            if (!objects.Contains(table))
            {
                throw UnsupportedSchema($"incomplete v30 (missing {table})");
            }
        }

        return true;
    }

    private static InvalidOperationException UnsupportedSchema(string version)
        => new($"SQLite schema '{version}' is not supported. This build requires schema v30; the existing database was not modified. No migration or backfill is performed.");
}
