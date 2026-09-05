using System.Text.Json;
using Microsoft.Data.Sqlite;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime.Agent;
using SelfClaw.Infrastructure.AiProviders.Abstractions;
using SelfClaw.Infrastructure.AiProviders.Models;

namespace SelfClaw.Infrastructure.Data.Sqlite;

internal static class SqliteMappings
{
    public static AiProviderConnection ReadAiProviderConnection(SqliteDataReader reader)
        => new(
            ReadGuid(reader, 0),
            reader.GetString(1),
            reader.GetString(2),
            (AiProviderKind)reader.GetInt32(3),
            new Uri(reader.GetString(4), UriKind.Absolute),
            (AiProviderAuthKind)reader.GetInt32(5),
            ReadStringDictionary(reader, 6),
            ReadJsonElementDictionary(reader, 7),
            ReadDateTimeOffset(reader, 8),
            ReadDateTimeOffset(reader, 9),
            reader.FieldCount <= 10 || reader.IsDBNull(10) || reader.GetInt32(10) != 0);

    public static AiModelProfile ReadAiModelProfile(SqliteDataReader reader)
        => new(
            ReadGuid(reader, 0),
            ReadGuid(reader, 1),
            reader.GetString(2),
            (AiProviderApiFormat)reader.GetInt32(3),
            reader.GetString(4),
            new AiSamplingOptions(
                !reader.IsDBNull(5) && reader.GetInt32(5) != 0,
                reader.IsDBNull(6) ? 0.7 : reader.GetDouble(6),
                !reader.IsDBNull(7) && reader.GetInt32(7) != 0,
                reader.IsDBNull(8) ? 0.7 : reader.GetDouble(8)),
            ReadJsonElementDictionary(reader, 9),
            ReadDateTimeOffset(reader, 10),
            ReadDateTimeOffset(reader, 11),
            reader.FieldCount <= 12 || reader.IsDBNull(12) || reader.GetInt32(12) != 0);

    public static AiModelProfileSelection ReadAiModelProfileSelection(SqliteDataReader reader)
        => new(
            reader.GetString(0),
            ReadGuid(reader, 1),
            ReadDateTimeOffset(reader, 2));

    public static ConversationRecord ReadConversation(SqliteDataReader reader)
        => new(
            ReadGuid(reader, 0),
            reader.GetString(1),
            reader.IsDBNull(2) ? null : ReadGuid(reader, 2),
            reader.IsDBNull(3) ? ConversationMode.Programming : (ConversationMode)reader.GetInt32(3),
            (ToolPermissionMode)reader.GetInt32(4),
            reader.IsDBNull(5) ? "build" : reader.GetString(5),
            ReadDateTimeOffset(reader, 9),
            ReadDateTimeOffset(reader, 10),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            (ConversationKind)reader.GetInt32(11),
            reader.IsDBNull(12) ? null : ReadGuid(reader, 12));

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
            reader.IsDBNull(12) ? null : reader.GetString(12),
            reader.IsDBNull(13) ? null : (ToolSourceKind)reader.GetInt32(13),
            reader.IsDBNull(14) ? null : reader.GetString(14),
            reader.IsDBNull(15) ? null : reader.GetString(15));

    public static WorkspaceRoot ReadWorkspaceRoot(SqliteDataReader reader)
        => new(
            ReadGuid(reader, 0),
            reader.GetString(1),
            reader.GetString(2),
            ReadDateTimeOffset(reader, 3),
            ReadDateTimeOffset(reader, 4),
            reader.IsDBNull(5) ? null : ReadGuid(reader, 5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            !reader.IsDBNull(8) && reader.GetInt32(8) != 0,
            reader.IsDBNull(9) ? null : ReadGuid(reader, 9),
            reader.IsDBNull(10) ? null : reader.GetString(10));

    public static ExtensionPackageRecord ReadExtensionPackage(SqliteDataReader reader)
        => new(
            (ExtensionKind)reader.GetInt32(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetString(7),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            !reader.IsDBNull(9) && reader.GetInt32(9) != 0,
            reader.IsDBNull(10) ? null : reader.GetString(10),
            reader.IsDBNull(11) ? null : ReadDateTimeOffset(reader, 11),
            ReadDateTimeOffset(reader, 12),
            ReadDateTimeOffset(reader, 13));

    public static McpServerConfigRecord ReadMcpServerConfig(SqliteDataReader reader)
        => new(
            reader.GetString(0),
            reader.GetString(1),
            (McpTransportKind)reader.GetInt32(2),
            reader.GetString(3),
            ReadStringDictionary(reader, 4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            !reader.IsDBNull(6) && reader.GetInt32(6) != 0,
            reader.GetInt64(7),
            ReadStringArray(reader, 8),
            (McpServerHealthStatus)reader.GetInt32(9),
            reader.IsDBNull(10) ? null : reader.GetString(10),
            reader.IsDBNull(11) ? null : ReadDateTimeOffset(reader, 11),
            ReadDateTimeOffset(reader, 12),
            ReadDateTimeOffset(reader, 13));

    private static Guid ReadGuid(SqliteDataReader reader, int ordinal)
        => Guid.Parse(reader.GetString(ordinal));

    private static DateTimeOffset ReadDateTimeOffset(SqliteDataReader reader, int ordinal)
        => DateTimeOffset.Parse(reader.GetString(ordinal), System.Globalization.CultureInfo.InvariantCulture);

    private static IReadOnlyDictionary<string, string> ReadStringDictionary(SqliteDataReader reader, int ordinal)
        => JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(ordinal)) ?? [];

    private static IReadOnlyDictionary<string, JsonElement> ReadJsonElementDictionary(SqliteDataReader reader, int ordinal)
        => JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(reader.GetString(ordinal)) ?? [];

    private static IReadOnlyList<string> ReadStringArray(SqliteDataReader reader, int ordinal)
        => JsonSerializer.Deserialize<string[]>(reader.GetString(ordinal)) ?? [];
}
