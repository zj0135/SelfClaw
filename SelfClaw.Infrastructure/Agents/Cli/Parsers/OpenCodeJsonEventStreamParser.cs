using System.Text;
using System.Text.Json;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime.Agent;

namespace SelfClaw.Infrastructure.Agents.Cli.Parsers;

internal sealed class OpenCodeJsonEventStreamParser : CliStreamParser
{
    private readonly HashSet<string> _startedToolCalls = new(StringComparer.Ordinal);
    private readonly StringBuilder _assistantText = new();
    private bool _runStarted;

    protected override IEnumerable<AgentStreamEvent> HandleObject(JsonElement root)
    {
        var payload = root.TryGetProperty("part", out var part) && part.ValueKind == JsonValueKind.Object
            ? part
            : root;

        switch (GetString(root, "type"))
        {
            case "step_start":
                return StartRun(GetString(root, "sessionID") ?? GetString(payload, "sessionID"));
            case "text":
            {
                var message = GetString(payload, "text") ?? GetString(root, "text");
                if (string.IsNullOrEmpty(message))
                    return Array.Empty<AgentStreamEvent>();
                _assistantText.Append(message);
                return new AgentStreamEvent[]
                {
                    new AssistantTextDeltaEvent(GetString(payload, "id"), message),
                };
            }
            case "reasoning":
            {
                var thinking = GetString(payload, "text") ?? GetString(root, "text");
                return string.IsNullOrEmpty(thinking)
                    ? Array.Empty<AgentStreamEvent>()
                    : new AgentStreamEvent[]
                    {
                        new AssistantThinkingDeltaEvent(GetString(payload, "id"), thinking),
                    };
            }
            case "tool":
            case "tool_use":
                return HandleTool(payload);
            case "step_finish":
                return HandleUsage(root, payload);
            default:
                return Array.Empty<AgentStreamEvent>();
        }
    }

    private IEnumerable<AgentStreamEvent> HandleTool(JsonElement part)
    {
        var id = GetString(part, "callID") ?? GetString(part, "id");
        if (string.IsNullOrEmpty(id))
            return Array.Empty<AgentStreamEvent>();

        var toolName = GetString(part, "tool") ?? GetString(part, "name") ?? "tool";
        var state = part.TryGetProperty("state", out var candidate) ? candidate : default;
        var status = state.ValueKind == JsonValueKind.Object ? GetString(state, "status") : null;
        var events = new List<AgentStreamEvent>();

        if (_startedToolCalls.Add(id))
        {
            events.Add(new ToolCallStartedEvent(
                id,
                toolName,
                GetToolArguments(part, state),
                MapToolKind(toolName)));
        }

        if (IsTerminalToolStatus(status))
        {
            var content = state.ValueKind == JsonValueKind.Object
                ? GetString(state, "output") ?? GetString(state, "result")
                : null;
            var completion = string.Equals(status, "error", StringComparison.OrdinalIgnoreCase)
                ? ToolCallStatus.Failed
                : ToolCallStatus.Completed;
            events.Add(new ToolCallCompletedEvent(id, completion, BuildSummary(content), content));
        }

        return events;
    }

    private static string GetToolArguments(JsonElement part, JsonElement state)
    {
        if (state.ValueKind == JsonValueKind.Object
            && state.TryGetProperty("input", out var input)
            && input.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
            return input.GetRawText();

        if (part.TryGetProperty("input", out var partInput)
            && partInput.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
            return partInput.GetRawText();

        return "{}";
    }

    private static bool IsTerminalToolStatus(string? status) =>
        string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase)
        || string.Equals(status, "error", StringComparison.OrdinalIgnoreCase);

    private static ToolCallKind MapToolKind(string name) => name.ToLowerInvariant() switch
    {
        "read" => ToolCallKind.Read,
        "write" or "edit" or "patch" => ToolCallKind.Edit,
        "bash" or "shell" => ToolCallKind.Run,
        "grep" or "webfetch" or "websearch" => ToolCallKind.Search,
        "glob" or "list" or "ls" => ToolCallKind.List,
        _ => ToolCallKind.Other,
    };

    private static IEnumerable<AgentStreamEvent> HandleUsage(JsonElement root, JsonElement payload)
    {
        var source = FindUsageObject(root) ?? FindUsageObject(payload);
        if (source is not { } usage)
            return Array.Empty<AgentStreamEvent>();

        // OpenCode keeps cache buckets out of `input`, so the normalized total adds them back.
        var uncachedInput = GetInt(usage, "input") ?? GetInt(usage, "input_tokens");
        var output = GetInt(usage, "output") ?? GetInt(usage, "output_tokens");
        var cacheRead = GetCacheTokens(usage, "read");
        var cacheWrite = GetCacheTokens(usage, "write");
        var input = SumTokens(uncachedInput, cacheRead, cacheWrite);
        var cost = GetCostUsdMicros(payload, "cost") ?? GetCostUsdMicros(root, "cost");
        if (input is null && output is null && cacheRead is null && cacheWrite is null && cost is null)
            return Array.Empty<AgentStreamEvent>();

        return new AgentStreamEvent[]
        {
            new UsageReportedEvent(new TurnUsage(
                InputTokens: input,
                UncachedInputTokens: uncachedInput,
                CachedInputTokens: cacheRead,
                CacheWriteInputTokens: cacheWrite,
                OutputTokens: output,
                ReasoningTokens: GetInt(usage, "reasoning"),
                TotalTokens: GetInt(usage, "total") ?? GetInt(usage, "total_tokens") ??
                    (input is int totalInput && output is int totalOutput ? totalInput + totalOutput : null),
                ProviderCalls: 1,
                ContextTokens: input is int contextInput ? contextInput + (output ?? 0) : null,
                CostUsdMicros: cost,
                CostSource: cost is null ? TurnUsageCostSource.None : TurnUsageCostSource.ProviderReported)),
        };
    }

    private static int? GetCacheTokens(JsonElement usage, string bucket)
        => usage.TryGetProperty("cache", out var cache) && cache.ValueKind == JsonValueKind.Object
            ? GetInt(cache, bucket)
            : null;

    private static JsonElement? FindUsageObject(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;
        if (element.TryGetProperty("tokens", out var tokens) && tokens.ValueKind == JsonValueKind.Object)
            return tokens;
        if (element.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
            return usage;
        return null;
    }

    private IEnumerable<AgentStreamEvent> StartRun(string? sessionId)
    {
        if (_runStarted)
            return Array.Empty<AgentStreamEvent>();

        _runStarted = true;
        return new AgentStreamEvent[]
        {
            new RunStartedEvent(sessionId, Model: null, CliAgentKind.OpenCode),
            new RunStatusEvent(AgentRunStatus.Running),
        };
    }
}
