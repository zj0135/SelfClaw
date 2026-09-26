using System.Text.Json;
using SelfClaw.Core.Runtime;
using SelfClaw.Infrastructure.Data.Sqlite.Models;

namespace SelfClaw.Infrastructure.Data.Sqlite;

/// <summary>
/// Maps <see cref="ToolHookOutcome"/> onto the three <c>tool_runs</c> columns. The feedback and the
/// remaining fields are separated because replay rebuilds the host-generated "arguments modified"
/// note from the effective arguments plus the modifiers, while the stored feedback must stay exactly
/// what the hooks returned.
/// </summary>
internal static class ToolHookOutcomeColumns
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    internal static (string? EffectiveArgumentsJson, string? FeedbackJson, string? OutcomeJson) Split(
        ToolHookOutcome? outcome)
    {
        if (outcome is null)
        {
            return (null, null, null);
        }

        var feedbackJson = outcome.Feedback.Count == 0
            ? null
            : JsonSerializer.Serialize(
                outcome.Feedback.Select(feedback => new HookFeedbackJson(ToJson(feedback.Source), feedback.Text)).ToArray(),
                JsonOptions);
        var hasOutcome = outcome.ArgumentsModifiedBy.Count > 0 ||
            outcome.ApprovalRequiredBy.Count > 0 ||
            outcome.BlockedBy is not null ||
            outcome.BlockReason is not null ||
            outcome.IgnoredFailures.Count > 0;
        var outcomeJson = hasOutcome
            ? JsonSerializer.Serialize(
                new ToolHookOutcomeJson(
                    outcome.ArgumentsModifiedBy.Select(ToJson).ToArray(),
                    outcome.ApprovalRequiredBy.Select(ToJson).ToArray(),
                    outcome.BlockedBy is null ? null : ToJson(outcome.BlockedBy),
                    outcome.BlockReason,
                    outcome.IgnoredFailures
                        .Select(failure => new HookFailureJson(ToJson(failure.Source), failure.Kind, failure.Message))
                        .ToArray()),
                JsonOptions)
            : null;
        return (outcome.EffectiveArgumentsJson, feedbackJson, outcomeJson);
    }

    internal static ToolHookOutcome? Combine(string? effectiveArgumentsJson, string? feedbackJson, string? outcomeJson)
    {
        if (effectiveArgumentsJson is null && feedbackJson is null && outcomeJson is null)
        {
            return null;
        }

        var decoded = outcomeJson is null
            ? null
            : JsonSerializer.Deserialize<ToolHookOutcomeJson>(outcomeJson, JsonOptions);
        var feedback = feedbackJson is null
            ? []
            : (JsonSerializer.Deserialize<HookFeedbackJson[]>(feedbackJson, JsonOptions) ?? [])
                .Select(item => new HookFeedback(ToSource(item.Source), item.Text))
                .ToArray();
        return new ToolHookOutcome(
            effectiveArgumentsJson,
            decoded?.ArgumentsModifiedBy.Select(ToSource).ToArray() ?? [],
            decoded?.ApprovalRequiredBy.Select(ToSource).ToArray() ?? [],
            decoded?.BlockedBy is null ? null : ToSource(decoded.BlockedBy),
            decoded?.BlockReason,
            feedback,
            decoded?.IgnoredFailures
                .Select(failure => new HookFailureNotice(ToSource(failure.Source), failure.Kind, failure.Message))
                .ToArray() ?? []);
    }

    private static HookSourceJson ToJson(HookSource source) => new(source.PluginId, source.HookId);

    private static HookSource ToSource(HookSourceJson source) => new(source.PluginId, source.HookId);
}
