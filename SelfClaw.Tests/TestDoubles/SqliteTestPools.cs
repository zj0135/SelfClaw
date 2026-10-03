using Microsoft.Data.Sqlite;

namespace SelfClaw.Tests.TestDoubles;

/// <summary>
/// Clears the connection pool for one database file. <see cref="SqliteConnection.ClearAllPools"/> is
/// process-global and races with a concurrently running test that is opening a pooled connection,
/// which surfaces as <see cref="ObjectDisposedException"/> on the native sqlite3 handle. Every test
/// fixture uses a unique database path, so path-scoped clearing is sufficient and cannot perturb
/// another test's pool.
/// </summary>
internal static class SqliteTestPools
{
    internal static void ClearFor(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        // Must mirror SqliteDatabase.CreateConnection so the pool key matches the pooled connections.
        using var probe = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWrite,
            ForeignKeys = true,
            Pooling = true
        }.ToString());
        SqliteConnection.ClearPool(probe);
    }
}