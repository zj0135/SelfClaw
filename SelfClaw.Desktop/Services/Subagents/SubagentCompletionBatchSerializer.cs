using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SelfClaw.Core.Models;

namespace SelfClaw.Desktop.Services.Subagents;

internal sealed class SubagentCompletionBatchSerializer
{
    internal const int MaximumBatchBytes = 64 * 1024;
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    internal SubagentCompletionBatch Deserialize(SubagentDeliveryLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        var envelopes = lease.Deliveries.Select(DeserializeEnvelope).ToArray();
        var batch = new SubagentCompletionBatch(envelopes);
        if (Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(batch, SerializerOptions)) > MaximumBatchBytes)
        {
            throw new InvalidDataException("The leased Subagent completion batch exceeds 64 KiB.");
        }

        return batch;
    }

    private static SubagentCompletionEnvelope DeserializeEnvelope(SubagentDeliveryRecord delivery)
    {
        try
        {
            var envelope = JsonSerializer.Deserialize<SubagentCompletionEnvelope>(
                    delivery.EnvelopeJson,
                    SerializerOptions)
                ?? throw new InvalidDataException("A Subagent completion envelope is empty.");
            if (envelope.DeliveryId != delivery.Id ||
                envelope.TaskId != delivery.TaskId ||
                envelope.ParentTurnId != delivery.ParentTurnId)
            {
                throw new InvalidDataException("A Subagent completion envelope does not match its delivery row.");
            }

            return envelope;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("A Subagent completion envelope is invalid.", exception);
        }
    }
}
