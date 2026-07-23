using System.Text;
using System.Text.Json;
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

        var input = GetInt(usage, "input") ?? GetInt(usage, "input_tokens");
        var output = GetInt(usage, "output") ?? GetInt(usage, "output_tokens");
        return input is null && output is null
            ? Array.Empty<AgentStreamEvent>()
            : new AgentStreamEvent[] { new UsageReportedEvent(input, output) };
    }

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
