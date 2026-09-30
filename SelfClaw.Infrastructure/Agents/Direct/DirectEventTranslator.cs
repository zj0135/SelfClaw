using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.AI;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Core.Runtime.Agent;
using SelfClaw.Infrastructure.AiProviders;
using SelfClaw.Infrastructure.Agents.Direct.Tools;
using SelfClaw.Infrastructure.Agents.Direct.Tools.Models;

namespace SelfClaw.Infrastructure.Agents.Direct;

/// <summary>
/// One turn's stream translation: it reduces Microsoft.Extensions.AI updates into the shared
/// <see cref="AgentStreamEvent"/> stream - text and reasoning deltas, tool call/result pairs, and one
/// aggregated usage report - so the runtime holds the turn's control flow instead of provider payload
/// handling. It also keeps the turn's final text, which the runtime reports in the terminal event.
/// </summary>
internal sealed class DirectEventTranslator(ChannelWriter<AgentStreamEvent> writer)
{
    private readonly ChannelWriter<AgentStreamEvent> _writer = writer;
    private readonly StringBuilder _finalText = new();
    private readonly HashSet<string> _startedCalls = new(StringComparer.Ordinal);
    private readonly TurnUsageAccumulator _usage = new();
    private static readonly string[] CacheWriteTokenKeys =
    [
        "CacheCreationInputTokens",
        "cache_creation_input_tokens",
        "cacheCreationInputTokens"
    ];
    private AiModelConfiguration? _usageConfiguration;
    private string? _usageModel;
    private int? _contextWindowTokens;
    private bool _usageWritten;

    public string FinalText => _finalText.ToString();

    public string? FinalTextOrNull => _finalText.Length == 0 ? null : _finalText.ToString();

    public bool HasFinalText => _finalText.Length > 0;

    public TurnUsage? Usage => BuildUsage();

    /// <summary>
    /// Supplies the pricing, model id and context window the accumulated usage is reported with.
    /// The translator has no provider or model knowledge of its own.
    /// </summary>
    public void ConfigureUsage(AiModelConfiguration? configuration, string? model, int? contextWindowTokens)
    {
        _usageConfiguration = configuration;
        _usageModel = model;
        _contextWindowTokens = contextWindowTokens;
    }

    public void TranslateUpdate(
        ChatResponseUpdate update,
        IReadOnlyDictionary<string, DirectToolBinding> bindings,
        DirectToolInvoker? invoker)
    {
        var blockId = string.IsNullOrWhiteSpace(update.MessageId)
            ? "direct-response"
            : update.MessageId;

        foreach (var content in update.Contents)
        {
            switch (content)
            {
                case TextContent text when !string.IsNullOrEmpty(text.Text):
                    _finalText.Append(text.Text);
                    _writer.TryWrite(new AssistantTextDeltaEvent(blockId, text.Text));
                    break;

                case TextReasoningContent reasoning when !string.IsNullOrEmpty(reasoning.Text):
                    _writer.TryWrite(new AssistantThinkingDeltaEvent(blockId, reasoning.Text));
                    break;

                case FunctionCallContent call when _startedCalls.Add(call.CallId):
                    bindings.TryGetValue(call.Name, out var binding);
                    var descriptor = binding?.Descriptor;
                    var toolKind = descriptor?.Kind ?? ToolCallKind.Other;
                    _writer.TryWrite(new ToolCallStartedEvent(
                        call.CallId,
                        call.Name,
                        JsonSerializer.Serialize(call.Arguments),
                        toolKind,
                        descriptor?.SourceKind ?? ToolSourceKind.BuiltIn,
                        descriptor?.SourceId,
                        descriptor?.DisplayName));
                    break;

                case FunctionResultContent result:
                    var (status, summary, detail) = DescribeToolResult(result);
                    _writer.TryWrite(new ToolCallCompletedEvent(
                        result.CallId,
                        status,
                        summary,
                        detail,
                        invoker?.TryTakeOutcome(result.CallId)));
                    break;

                case UsageContent usage:
                    ObserveUsage(usage.Details);
                    break;
            }
        }
    }

    /// <summary>Reports the turn's usage once; later calls are no-ops.</summary>
    public void ReportUsage()
    {
        if (_usageWritten)
        {
            return;
        }

        _usageWritten = true;
        if (BuildUsage() is { } usage)
        {
            _writer.TryWrite(new UsageReportedEvent(usage));
        }
    }

    /// <summary>Records one provider call; every observation counts as one request.</summary>
    private void ObserveUsage(UsageDetails details)
    {
        var inputTokens = ClampTokensOrNull(details.InputTokenCount);
        var outputTokens = ClampTokensOrNull(details.OutputTokenCount);
        _usage.Observe(new TurnUsage(
            InputTokens: inputTokens,
            CachedInputTokens: ClampTokensOrNull(details.CachedInputTokenCount),
            CacheWriteInputTokens: ReadCacheWriteTokens(details),
            OutputTokens: outputTokens,
            ReasoningTokens: ClampTokensOrNull(details.ReasoningTokenCount),
            TotalTokens: ClampTokensOrNull(details.TotalTokenCount),
            ProviderCalls: 1,
            ContextTokens: inputTokens is int input ? input + (outputTokens ?? 0) : null,
            AdditionalCountsJson: details.AdditionalCounts is { Count: > 0 } counts
                ? JsonSerializer.Serialize(counts)
                : null));
    }

    private TurnUsage? BuildUsage()
    {
        if (_usage.Build() is not { } usage)
        {
            return null;
        }

        var cost = TurnUsageCostCalculator.ComputeCostUsdMicros(usage, _usageConfiguration);
        return usage with
        {
            Model = _usageModel ?? usage.Model,
            ContextWindowTokens = _contextWindowTokens ?? usage.ContextWindowTokens,
            CostUsdMicros = cost,
            CostSource = cost is null ? TurnUsageCostSource.None : TurnUsageCostSource.Estimated
        };
    }

    /// <summary>
    /// The Anthropic SDK reports cache-write tokens through <see cref="UsageDetails.AdditionalCounts"/>
    /// instead of a first-class property; accept the snake-case spelling as well so a future SDK
    /// revision cannot silently drop them.
    /// </summary>
    private static int? ReadCacheWriteTokens(UsageDetails details)
    {
        if (details.AdditionalCounts is not { Count: > 0 } counts)
        {
            return null;
        }

        foreach (var key in CacheWriteTokenKeys)
        {
            if (counts.TryGetValue(key, out var value))
            {
                return ClampTokensOrNull(value);
            }
        }

        return null;
    }

    /// <summary>
    /// Reads the transcript-facing status, summary and display detail out of a tool result. A provider-side
    /// exception only happens when the invoker rethrew (its own fault budget is spent, or the failure is not
    /// a tool fault at all), so it must not be reported as a completed call.
    /// </summary>
    private static (ToolCallStatus Status, string? Summary, string? Detail) DescribeToolResult(
        FunctionResultContent content)
    {
        if (content.Exception is not null)
        {
            // Replayed on later turns, so only the message is kept; the stack trace would be prompt noise.
            return (ToolCallStatus.Failed, content.Exception.Message, content.Exception.Message);
        }

        return content.Result switch
        {
            DirectToolResult result => (result.Status, result.Summary, result.Detail),
            _ => (ToolCallStatus.Failed, "The tool returned an invalid Direct result.", null)
        };
    }

    private static int? ClampTokensOrNull(long? tokens)
        => tokens is long value ? (int)Math.Clamp(value, 0, int.MaxValue) : null;
}
