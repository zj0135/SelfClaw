using System.IO;
using System.Text.Json;
using SelfClaw.Core.Models;

namespace SelfClaw.Desktop.Services.Automation;

internal sealed class AutomationRunSnapshotSerializer
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    internal string Serialize(AutomationCapabilitySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return JsonSerializer.Serialize(snapshot, SerializerOptions);
    }

    internal AutomationCapabilitySnapshot Deserialize(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new InvalidDataException("The automation capability snapshot is empty.");
        }

        try
        {
            var snapshot = JsonSerializer.Deserialize<AutomationCapabilitySnapshot>(json, SerializerOptions)
                ?? throw new InvalidDataException("The automation capability snapshot is empty.");
            if (snapshot.Version != 1)
            {
                throw new InvalidDataException($"Automation capability snapshot version '{snapshot.Version}' is not supported.");
            }

            return snapshot;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The automation capability snapshot is invalid.", exception);
        }
    }
}
