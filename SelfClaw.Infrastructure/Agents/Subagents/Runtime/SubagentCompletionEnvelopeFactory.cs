using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SelfClaw.Core.Models;

namespace SelfClaw.Infrastructure.Agents.Subagents.Runtime;

internal sealed class SubagentCompletionEnvelopeFactory
{
    internal const int MaximumEnvelopeBytes = 32 * 1024;
    private static readonly TimeSpan InitialDeliveryDelay = TimeSpan.FromSeconds(2);

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    internal SubagentDeliveryRecord Create(SubagentTaskRecord task)
    {
        ArgumentNullException.ThrowIfNull(task);
        if (!IsTerminal(task.Status) || task.CompletedAtUtc is not DateTimeOffset completedAtUtc)
        {
            throw new ArgumentException("A completion envelope requires a terminal task.", nameof(task));
        }

        var deliveryId = Guid.NewGuid();
        double? duration = task.StartedAtUtc is DateTimeOffset startedAtUtc
            ? (completedAtUtc - startedAtUtc).TotalMilliseconds
            : null;
        var envelope = new SubagentCompletionEnvelope(
            1,
            deliveryId,
            task.Id,
            task.ParentTurnId,
            task.ChildConversationId,
            new SubagentIdentity(task.SubagentId, task.SubagentName),
            task.TaskText,
            task.Status,
            task.Attempt,
            new SubagentCompletionResult(
                task.FinalText,
                Truncated: false,
                task.ErrorCode,
                task.ErrorMessage),
            new SubagentUsage(task.InputTokens, task.OutputTokens),
            new SubagentTiming(task.QueuedAtUtc, task.StartedAtUtc, completedAtUtc, duration));
        var envelopeJson = SerializeWithinLimit(envelope);
        var now = completedAtUtc;
        return new SubagentDeliveryRecord(
            deliveryId,
            task.Id,
            task.ParentConversationId,
            task.ParentTurnId,
            SubagentDeliveryStatus.Pending,
            envelopeJson,
            Encoding.UTF8.GetByteCount(envelopeJson),
            LeaseToken: null,
            LeasedUntilUtc: null,
            AttemptCount: 0,
            NextAttemptAtUtc: now + InitialDeliveryDelay,
            ContinuationTurnId: null,
            LastError: null,
            CreatedAtUtc: now,
            UpdatedAtUtc: now,
            DeliveredAtUtc: null,
            DeadLetteredAtUtc: null);
    }

    private static string SerializeWithinLimit(SubagentCompletionEnvelope envelope)
    {
        var json = Serialize(envelope);
        if (Encoding.UTF8.GetByteCount(json) <= MaximumEnvelopeBytes)
        {
            return json;
        }

        var candidate = envelope with
        {
            Result = envelope.Result with { Truncated = true }
        };
        candidate = FitField(
            candidate,
            candidate.Result.FinalText,
            (current, value) => current with { Result = current.Result with { FinalText = value } });
        json = Serialize(candidate);
        if (Encoding.UTF8.GetByteCount(json) <= MaximumEnvelopeBytes)
        {
            return json;
        }

        candidate = FitField(candidate, candidate.Task, (current, value) => current with { Task = value ?? string.Empty });
        json = Serialize(candidate);
        if (Encoding.UTF8.GetByteCount(json) <= MaximumEnvelopeBytes)
        {
            return json;
        }

        candidate = FitField(
            candidate,
            candidate.Result.ErrorMessage,
            (current, value) => current with { Result = current.Result with { ErrorMessage = value } });
        json = Serialize(candidate);
        if (Encoding.UTF8.GetByteCount(json) > MaximumEnvelopeBytes)
        {
            throw new InvalidOperationException("Subagent completion metadata exceeds the envelope size limit.");
        }

        return json;
    }

    private static SubagentCompletionEnvelope FitField(
        SubagentCompletionEnvelope envelope,
        string? value,
        Func<SubagentCompletionEnvelope, string?, SubagentCompletionEnvelope> update)
    {
        if (string.IsNullOrEmpty(value))
        {
            return envelope;
        }

        var runes = value.EnumerateRunes().Select(rune => rune.ToString()).ToArray();
        var empty = update(envelope, string.Empty);
        if (Encoding.UTF8.GetByteCount(Serialize(empty)) > MaximumEnvelopeBytes)
        {
            return empty;
        }

        var lower = 0;
        var upper = runes.Length;
        while (lower < upper)
        {
            var count = lower + ((upper - lower + 1) / 2);
            var candidate = update(envelope, string.Concat(runes.Take(count)));
            if (Encoding.UTF8.GetByteCount(Serialize(candidate)) <= MaximumEnvelopeBytes)
            {
                lower = count;
            }
            else
            {
                upper = count - 1;
            }
        }

        return update(envelope, string.Concat(runes.Take(lower)));
    }

    private static string Serialize(SubagentCompletionEnvelope envelope)
        => JsonSerializer.Serialize(envelope, SerializerOptions);

    private static bool IsTerminal(SubagentTaskStatus status)
        => status is SubagentTaskStatus.Succeeded
            or SubagentTaskStatus.Failed
            or SubagentTaskStatus.Cancelled
            or SubagentTaskStatus.Interrupted;
}
