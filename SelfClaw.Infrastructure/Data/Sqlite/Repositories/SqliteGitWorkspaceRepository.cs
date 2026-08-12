using System.Globalization;
using Microsoft.Data.Sqlite;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Infrastructure.Data.Sqlite;

namespace SelfClaw.Infrastructure.Data.Sqlite.Repositories;

internal sealed class SqliteGitWorkspaceRepository : IGitWorkspaceStore
{
    private readonly SqliteDatabase _database;

    public SqliteGitWorkspaceRepository(SqliteDatabase database)
    {
        _database = database;
    }

    public async Task<GitCheckoutRecord?> GetCheckoutAsync(
        Guid workspaceRootId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT workspace_root_id, repository_id, is_managed, owner_conversation_id,
       source_workspace_root_id, branch_name, base_branch_name, base_commit_sha,
       created_at_utc, updated_at_utc
FROM git_checkouts
WHERE workspace_root_id = $workspaceRootId;";
        command.Parameters.AddWithValue("$workspaceRootId", workspaceRootId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadCheckout(reader)
            : null;
    }

    public async Task<GitCheckoutRecord?> GetConversationCheckoutAsync(
        Guid conversationId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = @"
SELECT workspace_root_id, repository_id, is_managed, owner_conversation_id,
       source_workspace_root_id, branch_name, base_branch_name, base_commit_sha,
       created_at_utc, updated_at_utc
FROM git_checkouts
WHERE owner_conversation_id = $conversationId
LIMIT 1;";
        command.Parameters.AddWithValue("$conversationId", conversationId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadCheckout(reader)
            : null;
    }

    public async Task SaveAsync(
        GitRepositoryRecord repository,
        GitCheckoutRecord checkout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(checkout);

        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await using (var repositoryCommand = connection.CreateCommand())
        {
            repositoryCommand.Transaction = transaction;
            repositoryCommand.CommandText = @"
INSERT INTO git_repositories(id, name, common_directory, created_at_utc, updated_at_utc)
VALUES($id, $name, $commonDirectory, $createdAt, $updatedAt)
ON CONFLICT(id) DO UPDATE SET
    name = excluded.name,
    common_directory = excluded.common_directory,
    updated_at_utc = excluded.updated_at_utc;";
            repositoryCommand.Parameters.AddWithValue("$id", repository.Id.ToString("D"));
            repositoryCommand.Parameters.AddWithValue("$name", repository.Name);
            repositoryCommand.Parameters.AddWithValue("$commonDirectory", repository.CommonDirectory);
            repositoryCommand.Parameters.AddWithValue("$createdAt", repository.CreatedAtUtc.ToString("O"));
            repositoryCommand.Parameters.AddWithValue("$updatedAt", repository.UpdatedAtUtc.ToString("O"));
            await repositoryCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var checkoutCommand = connection.CreateCommand())
        {
            checkoutCommand.Transaction = transaction;
            checkoutCommand.CommandText = @"
INSERT INTO git_checkouts(
    workspace_root_id, repository_id, is_managed, owner_conversation_id,
    source_workspace_root_id, branch_name, base_branch_name, base_commit_sha,
    created_at_utc, updated_at_utc)
VALUES(
    $workspaceRootId, $repositoryId, $isManaged, $ownerConversationId,
    $sourceWorkspaceRootId, $branchName, $baseBranchName, $baseCommitSha,
    $createdAt, $updatedAt)
ON CONFLICT(workspace_root_id) DO UPDATE SET
    repository_id = excluded.repository_id,
    is_managed = excluded.is_managed,
    owner_conversation_id = excluded.owner_conversation_id,
    source_workspace_root_id = excluded.source_workspace_root_id,
    branch_name = excluded.branch_name,
    base_branch_name = excluded.base_branch_name,
    base_commit_sha = excluded.base_commit_sha,
    updated_at_utc = excluded.updated_at_utc;";
            checkoutCommand.Parameters.AddWithValue("$workspaceRootId", checkout.WorkspaceRootId.ToString("D"));
            checkoutCommand.Parameters.AddWithValue("$repositoryId", checkout.RepositoryId.ToString("D"));
            checkoutCommand.Parameters.AddWithValue("$isManaged", checkout.IsManaged ? 1 : 0);
            checkoutCommand.Parameters.AddWithValue("$ownerConversationId", checkout.OwnerConversationId?.ToString("D") ?? (object)DBNull.Value);
            checkoutCommand.Parameters.AddWithValue("$sourceWorkspaceRootId", checkout.SourceWorkspaceRootId?.ToString("D") ?? (object)DBNull.Value);
            checkoutCommand.Parameters.AddWithValue("$branchName", checkout.BranchName);
            checkoutCommand.Parameters.AddWithValue("$baseBranchName", checkout.BaseBranchName ?? (object)DBNull.Value);
            checkoutCommand.Parameters.AddWithValue("$baseCommitSha", checkout.BaseCommitSha ?? (object)DBNull.Value);
            checkoutCommand.Parameters.AddWithValue("$createdAt", checkout.CreatedAtUtc.ToString("O"));
            checkoutCommand.Parameters.AddWithValue("$updatedAt", checkout.UpdatedAtUtc.ToString("O"));
            await checkoutCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task ReleaseConversationAsync(
        Guid conversationId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = @"
UPDATE git_checkouts
SET is_managed = 0, owner_conversation_id = NULL, updated_at_utc = $updatedAt
WHERE owner_conversation_id = $conversationId;";
        command.Parameters.AddWithValue("$conversationId", conversationId.ToString("D"));
        command.Parameters.AddWithValue("$updatedAt", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteCheckoutAsync(
        Guid workspaceRootId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM git_checkouts WHERE workspace_root_id = $workspaceRootId;";
        command.Parameters.AddWithValue("$workspaceRootId", workspaceRootId.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static GitCheckoutRecord ReadCheckout(SqliteDataReader reader)
        => new(
            ReadGuid(reader, 0),
            ReadGuid(reader, 1),
            reader.GetInt32(2) != 0,
            ReadNullableGuid(reader, 3),
            ReadNullableGuid(reader, 4),
            reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            ReadDateTimeOffset(reader, 8),
            ReadDateTimeOffset(reader, 9));

    private static Guid ReadGuid(SqliteDataReader reader, int ordinal)
        => Guid.Parse(reader.GetString(ordinal));

    private static Guid? ReadNullableGuid(SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : Guid.Parse(reader.GetString(ordinal));

    private static DateTimeOffset ReadDateTimeOffset(SqliteDataReader reader, int ordinal)
        => DateTimeOffset.Parse(reader.GetString(ordinal), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
