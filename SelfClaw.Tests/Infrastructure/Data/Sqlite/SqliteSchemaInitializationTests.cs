using FluentAssertions;
using Microsoft.Data.Sqlite;
using SelfClaw.Infrastructure.Data.Sqlite;
using SelfClaw.Tests.TestDoubles;

namespace SelfClaw.Tests.Infrastructure.Data.Sqlite;

public sealed class SqliteSchemaInitializationTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(20)]
    [InlineData(21)]
    [InlineData(22)]
    [InlineData(23)]
    [InlineData(24)]
    [InlineData(25)]
    [InlineData(26)]
    [InlineData(27)]
    [InlineData(28)]
    [InlineData(29)]
    [InlineData(31)]
    [InlineData(99)]
    [InlineData(null)]
    public async Task Existing_non_target_schema_is_rejected_without_modifying_any_bytes_or_files(int? version)
    {
        using var fixture = new ConversationPersistenceFixture();
        Directory.CreateDirectory(fixture.RootPath);
        await using (var connection = new SqliteConnection($"Data Source={fixture.Paths.DatabasePath};Pooling=False"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE profiles(id TEXT PRIMARY KEY, value TEXT);
                INSERT INTO profiles VALUES('keep', 'do not migrate');
                CREATE TABLE conversations_new(id TEXT PRIMARY KEY, title TEXT);
                INSERT INTO conversations_new VALUES('leftover', 'do not clean');
                CREATE TABLE tool_runs(id TEXT PRIMARY KEY, after_segment_index INTEGER);
                INSERT INTO tool_runs VALUES('tool', 7);
                """;
            await command.ExecuteNonQueryAsync();
            if (version is not null)
            {
                command.CommandText = "CREATE TABLE schema_versions(version INTEGER PRIMARY KEY, applied_at_utc TEXT); INSERT INTO schema_versions VALUES($version, 'original');";
                command.Parameters.AddWithValue("$version", version.Value);
                await command.ExecuteNonQueryAsync();
            }
        }

        var bytes = await File.ReadAllBytesAsync(fixture.Paths.DatabasePath);
        var files = Directory.GetFiles(fixture.RootPath).Order().ToArray();
        var rejected = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Database.EnsureInitializedAsync());
        rejected.Message.Should().Contain("requires schema v30").And.Contain("not modified");
        (await File.ReadAllBytesAsync(fixture.Paths.DatabasePath)).Should().Equal(bytes);
        Directory.GetFiles(fixture.RootPath).Order().Should().Equal(files);
    }

    [Fact]
    public async Task Empty_or_incomplete_target_version_marker_is_rejected_without_repair()
    {
        using var fixture = new ConversationPersistenceFixture();
        Directory.CreateDirectory(fixture.RootPath);
        await using (var connection = new SqliteConnection($"Data Source={fixture.Paths.DatabasePath};Pooling=False"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE schema_versions(version INTEGER PRIMARY KEY, applied_at_utc TEXT); INSERT INTO schema_versions VALUES(30, 'original');";
            await command.ExecuteNonQueryAsync();
        }

        var bytes = await File.ReadAllBytesAsync(fixture.Paths.DatabasePath);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Database.EnsureInitializedAsync());
        (await File.ReadAllBytesAsync(fixture.Paths.DatabasePath)).Should().Equal(bytes);
    }

    [Fact]
    public async Task Fresh_DDL_and_version_marker_roll_back_together_when_before_commit_fails()
    {
        using var fixture = new ConversationPersistenceFixture();
        var failing = new SqliteDatabase(fixture.Paths, null, (stage, _) => throw new InvalidOperationException(stage));
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => failing.EnsureInitializedAsync());
        exception.Message.Should().Be("schema-before-commit");
        await using (var connection = new SqliteConnection($"Data Source={fixture.Paths.DatabasePath};Mode=ReadOnly;Pooling=False"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM sqlite_schema WHERE name NOT LIKE 'sqlite_%';";
            (await command.ExecuteScalarAsync()).Should().Be(0L);
        }

        await fixture.Database.EnsureInitializedAsync();
        (await fixture.ScalarAsync("SELECT COUNT(*) FROM schema_versions WHERE version = 30;")).Should().Be(1L);
        (await fixture.ScalarAsync("SELECT COUNT(*) FROM schema_versions;")).Should().Be(1L);
    }

    [Fact]
    public async Task Target_v30_reopens_read_only_and_has_no_legacy_message_columns()
    {
        using var fixture = new ConversationPersistenceFixture();
        await fixture.Database.EnsureInitializedAsync();
        SqliteTestPools.ClearFor(fixture.Paths.DatabasePath);
        var before = await File.ReadAllBytesAsync(fixture.Paths.DatabasePath);
        await new SqliteDatabase(fixture.Paths).EnsureInitializedAsync();
        (await File.ReadAllBytesAsync(fixture.Paths.DatabasePath)).Should().Equal(before);
        (await fixture.ScalarAsync("SELECT COUNT(*) FROM pragma_table_info('messages') WHERE name IN ('usage', 'duration_ms', 'error_message', 'input_tokens', 'output_tokens');")).Should().Be(0L);
        (await fixture.ScalarAsync("SELECT COUNT(*) FROM pragma_table_info('messages') WHERE name IN ('turn_id', 'sequence') AND \"notnull\" = 1;")).Should().Be(2L);
        (await fixture.ScalarAsync("SELECT COUNT(*) FROM pragma_table_info('turn_usage') WHERE name = 'turn_id' AND pk = 1;")).Should().Be(1L);
        (await fixture.ScalarAsync("SELECT COUNT(*) FROM pragma_foreign_key_list('subagent_tasks') WHERE \"from\" = 'parent_turn_id';")).Should().Be(0L);
        (await fixture.ScalarAsync("SELECT COUNT(*) FROM pragma_foreign_key_check;")).Should().Be(0L);
    }
}
