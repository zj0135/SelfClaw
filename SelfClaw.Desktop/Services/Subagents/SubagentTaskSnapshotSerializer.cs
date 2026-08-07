using System.Text.Json;
using System.IO;
using SelfClaw.Desktop.Services.Subagents.Models;

namespace SelfClaw.Desktop.Services.Subagents;

internal sealed class SubagentTaskSnapshotSerializer
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    internal string Serialize(SubagentDefinitionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return JsonSerializer.Serialize(snapshot, SerializerOptions);
    }

    internal string Serialize(SubagentParentExecutionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return JsonSerializer.Serialize(snapshot, SerializerOptions);
    }

    internal SubagentDefinitionSnapshot DeserializeDefinition(string json)
        => Deserialize<SubagentDefinitionSnapshot>(json, "Subagent definition");

    internal SubagentParentExecutionSnapshot DeserializeParent(string json)
        => Deserialize<SubagentParentExecutionSnapshot>(json, "parent execution");

    private static T Deserialize<T>(string json, string snapshotName)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new InvalidDataException($"The {snapshotName} snapshot is empty.");
        }

        try
        {
            return JsonSerializer.Deserialize<T>(json, SerializerOptions)
                ?? throw new InvalidDataException($"The {snapshotName} snapshot is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"The {snapshotName} snapshot is invalid.", exception);
        }
    }
}
