using FluentAssertions;
using Microsoft.Data.Sqlite;
using SelfClaw.Core.Models;
using SelfClaw.Infrastructure.Data.Sqlite;
using SelfClaw.Infrastructure.Data.Sqlite.Repositories;
using SelfClaw.Infrastructure.Options;

namespace SelfClaw.Tests.Infrastructure.Data.Sqlite.Repositories;

public sealed class SqliteExtensionRepositoryTests : IDisposable
{
    private readonly string _rootPath;

    public SqliteExtensionRepositoryTests()
    {
        _rootPath = Path.Combine(Path.GetTempPath(), "SelfClawTests", Guid.NewGuid().ToString("N"));
    }

    [Fact]
    public async Task Repository_round_trips_packages_and_mcp_servers()
    {
        var repository = CreateRepository();
        var now = DateTimeOffset.UtcNow;
        var package = new ExtensionPackageRecord(
            ExtensionKind.Skill,
            "code-review",
            "Code Review",
            "1.0.0",
            "Reviews source changes.",
            Path.Combine(_rootPath, "skills", "code-review"),
            "sha256:package",
            "{\"id\":\"code-review\"}",
            null,
            true,
            "[\"read_workspace\"]",
            now,
            now,
            now);
        var server = new McpServerConfigRecord(
            "github",
            "GitHub",
            McpTransportKind.Http,
            "{\"endpoint\":\"https://mcp.example.test\",\"transportMode\":\"auto\",\"connectionTimeoutSeconds\":15}",
            new Dictionary<string, string> { ["headers.Authorization"] = "secret:github" },
            null,
            true,
            50,
            ["search_issues"],
            McpServerHealthStatus.Ready,
            null,
            now,
            now,
            now);

        await repository.UpsertPackageAsync(package);
        var storedServer = await repository.UpsertMcpServerAsync(server);

        (await repository.ListPackagesAsync()).Should().ContainSingle().Which.Should().Be(package);
        (await repository.GetPackageAsync(ExtensionKind.Skill, package.Id)).Should().Be(package);
        storedServer.ConfigRevision.Should().Be(1);
        (await repository.GetMcpServerAsync(server.Id)).Should().BeEquivalentTo(storedServer);

        await repository.SetPackageEnabledAsync(ExtensionKind.Skill, package.Id, false);
        await repository.SetMcpServerEnabledAsync(server.Id, false);
        (await repository.GetPackageAsync(ExtensionKind.Skill, package.Id))!.IsEnabled.Should().BeFalse();
        (await repository.GetMcpServerAsync(server.Id))!.IsEnabled.Should().BeFalse();

        await repository.DeletePackageAsync(ExtensionKind.Skill, package.Id);
        await repository.DeleteMcpServerAsync(server.Id);
        (await repository.ListPackagesAsync()).Should().BeEmpty();
        (await repository.ListMcpServersAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Mcp_upsert_increments_revision_only_when_connection_configuration_changes()
    {
        var repository = CreateRepository();
        var now = DateTimeOffset.UtcNow;
        var initial = new McpServerConfigRecord(
            "local-tools",
            "Local tools",
            McpTransportKind.Stdio,
            "{\"command\":\"node\",\"arguments\":[\"server.js\"]}",
            new Dictionary<string, string>(),
            null,
            true,
            1,
            [],
            McpServerHealthStatus.Unknown,
            null,
            null,
            now,
            now);

        var first = await repository.UpsertMcpServerAsync(initial);
        var healthUpdate = await repository.UpsertMcpServerAsync(first with
        {
            DisplayName = "Local developer tools",
            DiscoveredTools = ["read_project"],
            LastStatus = McpServerHealthStatus.Ready,
            LastCheckedAtUtc = now.AddMinutes(1),
            UpdatedAtUtc = now.AddMinutes(1)
        });
        var configurationUpdate = await repository.UpsertMcpServerAsync(healthUpdate with
        {
            SettingsJson = "{\"command\":\"node\",\"arguments\":[\"server-v2.js\"]}",
            UpdatedAtUtc = now.AddMinutes(2)
        });
        var credentialUpdate = await repository.UpsertMcpServerAsync(configurationUpdate with
        {
            CredentialRefs = new Dictionary<string, string> { ["environment.API_TOKEN"] = "secret:token" },
            UpdatedAtUtc = now.AddMinutes(3)
        });

        first.ConfigRevision.Should().Be(1);
        healthUpdate.ConfigRevision.Should().Be(1);
        configurationUpdate.ConfigRevision.Should().Be(2);
        credentialUpdate.ConfigRevision.Should().Be(3);
    }

    [Fact]
    public async Task Initialize_migrates_v21_data_and_adds_extension_schema_without_losing_rows()
    {
        var storagePaths = CreateStoragePaths();
        Directory.CreateDirectory(_rootPath);
        var conversationId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var toolRunId = Guid.NewGuid();

        await using (var connection = new SqliteConnection($"Data Source={storagePaths.DatabasePath}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = @"
CREATE TABLE schema_versions (version INTEGER NOT NULL PRIMARY KEY, applied_at_utc TEXT NOT NULL);
INSERT INTO schema_versions(version, applied_at_utc) VALUES(21, '2026-01-01T00:00:00.0000000+00:00');
CREATE TABLE conversations (
    id TEXT NOT NULL PRIMARY KEY, title TEXT NOT NULL, workspace_root_id TEXT NULL,
    mode INTEGER NOT NULL DEFAULT 0, tool_permission_mode INTEGER NOT NULL DEFAULT 0,
    agent_id TEXT NOT NULL DEFAULT 'build', channel_kind TEXT NULL,
    channel_conversation_id TEXT NULL, channel_display_name TEXT NULL,
    created_at_utc TEXT NOT NULL, updated_at_utc TEXT NOT NULL);
CREATE TABLE messages (
    id TEXT NOT NULL PRIMARY KEY, conversation_id TEXT NOT NULL, role INTEGER NOT NULL,
    markdown_content TEXT NOT NULL, status INTEGER NOT NULL, created_at_utc TEXT NOT NULL,
    updated_at_utc TEXT NOT NULL, agent_id TEXT NULL, agent_name TEXT NULL, agent_role TEXT NULL,
    input_tokens INTEGER NULL, output_tokens INTEGER NULL, duration_ms REAL NULL, error_message TEXT NULL);
CREATE TABLE tool_runs (
    id TEXT NOT NULL PRIMARY KEY, conversation_id TEXT NOT NULL, tool_name TEXT NOT NULL,
    arguments_json TEXT NOT NULL, status INTEGER NOT NULL, result_summary TEXT NULL,
    result_content TEXT NULL, correlation_id TEXT NULL, duration_ms REAL NULL,
    created_at_utc TEXT NOT NULL, updated_at_utc TEXT NOT NULL, agent_id TEXT NULL,
    message_id TEXT NULL, after_segment_index INTEGER NULL);
INSERT INTO conversations(id, title, created_at_utc, updated_at_utc)
VALUES($conversationId, 'Preserved conversation', $createdAt, $createdAt);
INSERT INTO messages(id, conversation_id, role, markdown_content, status, created_at_utc, updated_at_utc)
VALUES($messageId, $conversationId, 0, 'Preserved message', 1, $createdAt, $createdAt);
INSERT INTO tool_runs(id, conversation_id, tool_name, arguments_json, status, created_at_utc, updated_at_utc, message_id)
VALUES($toolRunId, $conversationId, 'read_file', '{}', 2, $createdAt, $createdAt, $messageId);";
            command.Parameters.AddWithValue("$conversationId", conversationId.ToString("D"));
            command.Parameters.AddWithValue("$messageId", messageId.ToString("D"));
            command.Parameters.AddWithValue("$toolRunId", toolRunId.ToString("D"));
            command.Parameters.AddWithValue("$createdAt", "2026-01-01T00:00:00.0000000+00:00");
            await command.ExecuteNonQueryAsync();
        }

        var repository = new SqliteExtensionRepository(new SqliteDatabase(storagePaths));
        await repository.InitializeAsync();

        await using var verification = new SqliteConnection($"Data Source={storagePaths.DatabasePath}");
        await verification.OpenAsync();
        (await ExecuteScalarAsync<long>(verification, "SELECT MAX(version) FROM schema_versions;")).Should().Be(23);
        (await ExecuteScalarAsync<long>(verification, "SELECT COUNT(*) FROM conversations;")).Should().Be(1);
        (await ExecuteScalarAsync<long>(verification, "SELECT COUNT(*) FROM messages;")).Should().Be(1);
        (await ExecuteScalarAsync<long>(verification, "SELECT COUNT(*) FROM tool_runs;")).Should().Be(1);
        (await ExecuteScalarAsync<long>(verification, "SELECT COUNT(*) FROM extension_packages;")).Should().Be(0);
        (await ExecuteScalarAsync<long>(verification, "SELECT COUNT(*) FROM mcp_server_configs;")).Should().Be(0);

        await using var sourceCommand = verification.CreateCommand();
        sourceCommand.CommandText = "SELECT source_kind, source_id, display_name FROM tool_runs WHERE id = $id;";
        sourceCommand.Parameters.AddWithValue("$id", toolRunId.ToString("D"));
        await using var sourceReader = await sourceCommand.ExecuteReaderAsync();
        (await sourceReader.ReadAsync()).Should().BeTrue();
        sourceReader.IsDBNull(0).Should().BeTrue();
        sourceReader.IsDBNull(1).Should().BeTrue();
        sourceReader.IsDBNull(2).Should().BeTrue();
    }

    public void Dispose()
    {
        if (!Directory.Exists(_rootPath))
        {
            return;
        }

        try
        {
            Directory.Delete(_rootPath, true);
        }
        catch (IOException)
        {
        }
    }

    private SqliteExtensionRepository CreateRepository()
        => new(new SqliteDatabase(CreateStoragePaths()));

    private StoragePaths CreateStoragePaths()
        => new(
            _rootPath,
            Path.Combine(_rootPath, "selfclaw.db"),
            Path.Combine(_rootPath, "secrets"));

    private static async Task<T> ExecuteScalarAsync<T>(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (T)(await command.ExecuteScalarAsync())!;
    }
}
