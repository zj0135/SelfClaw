using Microsoft.Data.Sqlite;
using SelfClaw.Core.Models;

namespace SelfClaw.Infrastructure.Data.Sqlite;

internal static class SqliteMappings
{
    public static ProviderProfile ReadProfile(SqliteDataReader reader)
        => new(
            ReadGuid(reader, 0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            !reader.IsDBNull(4) && reader.GetInt32(4) != 0,
            reader.IsDBNull(5) ? 0.7 : reader.GetDouble(5),
            !reader.IsDBNull(6) && reader.GetInt32(6) != 0,
            reader.IsDBNull(7) ? 0.7 : reader.GetDouble(7),
            (ApiStyle)reader.GetInt32(8),
            reader.GetString(9),
            ReadDateTimeOffset(reader, 10),
            ReadDateTimeOffset(reader, 11));

    public static ConversationRecord ReadConversation(SqliteDataReader reader)
        => new(
            ReadGuid(reader, 0),
            reader.GetString(1),
            ReadGuid(reader, 2),
            reader.IsDBNull(3) ? null : ReadGuid(reader, 3),
            reader.IsDBNull(4) ? ConversationMode.Programming : (ConversationMode)reader.GetInt32(4),
            (ToolPermissionMode)reader.GetInt32(5),
            reader.IsDBNull(6) ? "build" : reader.GetString(6),
            ReadDateTimeOffset(reader, 10),
            ReadDateTimeOffset(reader, 11),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.IsDBNull(9) ? null : reader.GetString(9));

    public static MessageRecord ReadMessage(SqliteDataReader reader)
        => new(
            ReadGuid(reader, 0),
            ReadGuid(reader, 1),
            (MessageRole)reader.GetInt32(2),
            reader.GetString(3),
            (MessageStatus)reader.GetInt32(4),
            ReadDateTimeOffset(reader, 5),
            ReadDateTimeOffset(reader, 6),
            reader.IsDBNull(7) ? null : ReadGuid(reader, 7),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.IsDBNull(9) ? null : reader.GetString(9),
            reader.IsDBNull(10) ? null : reader.GetInt32(10),
            reader.IsDBNull(11) ? null : reader.GetInt32(11),
            reader.IsDBNull(12) ? null : reader.GetDouble(12),
            reader.IsDBNull(13) ? null : reader.GetString(13));

    public static MessageAttachmentRecord ReadMessageAttachment(SqliteDataReader reader)
        => new(
            ReadGuid(reader, 0),
            ReadGuid(reader, 1),
            (MessageAttachmentKind)reader.GetInt32(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetInt64(6),
            ReadDateTimeOffset(reader, 7));

    public static ConversationContextSummaryRecord ReadConversationContextSummary(SqliteDataReader reader)
        => new(
            ReadGuid(reader, 0),
            reader.GetString(1),
            reader.IsDBNull(2) ? null : ReadGuid(reader, 2),
            reader.IsDBNull(3) ? null : ReadDateTimeOffset(reader, 3),
            reader.GetInt32(4),
            reader.GetInt32(5),
            ReadDateTimeOffset(reader, 6),
            ReadDateTimeOffset(reader, 7));

    public static ToolExecutionRecord ReadToolRun(SqliteDataReader reader)
        => new(
            ReadGuid(reader, 0),
            ReadGuid(reader, 1),
            reader.GetString(2),
            reader.GetString(3),
            (ToolExecutionStatus)reader.GetInt32(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetDouble(7),
            ReadDateTimeOffset(reader, 8),
            ReadDateTimeOffset(reader, 9),
            reader.IsDBNull(10) ? null : ReadGuid(reader, 10),
            reader.IsDBNull(11) ? null : ReadGuid(reader, 11),
            reader.IsDBNull(12) ? null : reader.GetInt32(12),
            reader.IsDBNull(13) ? null : reader.GetString(13));

    public static WorkspaceRoot ReadWorkspaceRoot(SqliteDataReader reader)
        => new(
            ReadGuid(reader, 0),
            reader.GetString(1),
            reader.GetString(2),
            ReadDateTimeOffset(reader, 3),
            ReadDateTimeOffset(reader, 4));

    private static Guid ReadGuid(SqliteDataReader reader, int ordinal)
        => Guid.Parse(reader.GetString(ordinal));

    private static DateTimeOffset ReadDateTimeOffset(SqliteDataReader reader, int ordinal)
        => DateTimeOffset.Parse(reader.GetString(ordinal), System.Globalization.CultureInfo.InvariantCulture);

}
