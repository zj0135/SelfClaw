using System.Text;
using System.Text.Json;
using SelfClaw.Core.Runtime.Agent;

namespace SelfClaw.Infrastructure.Agents.Cli.Parsers;

internal sealed class CodexJsonEventStreamParser : CliStreamParser
{
    private readonly HashSet<string> _startedToolCalls = new(StringComparer.Ordinal);
    private readonly HashSet<string> _emittedMessages = new(StringComparer.Ordinal);
    private readonly StringBuilder _assistantText = new();
    private bool _runStarted;

    protected override IEnumerable<AgentStreamEvent> HandleObject(JsonElement root)
    {
        switch (GetString(root, "type"))
        {
            case "thread.started":
                return StartRun(GetString(root, "thread_id"));
            case "turn.started":
                return new AgentStreamEvent[] { new RunStatusEvent(AgentRunStatus.Running) };
            case "item.started":
                return HandleItem(root, completed: false);
            case "item.completed":
                return HandleItem(root, completed: true);
            case "turn.completed":
                return HandleUsage(root);
            case "error":
            {
                var message = GetString(root, "message") ?? "The Codex agent reported an error.";
                return new AgentStreamEvent[]
                {
                    new RunCompletedEvent(
                        RunCompletionStatus.Failed,
                        FinalText: FinalTextOrNull(),
                        ErrorMessage: message),
                };
            }
            default:
                return Array.Empty<AgentStreamEvent>();
        }
    }

    private IEnumerable<AgentStreamEvent> HandleItem(JsonElement root, bool completed)
    {
        if (!root.TryGetProperty("item", out var item) || item.ValueKind != JsonValueKind.Object)
            return Array.Empty<AgentStreamEvent>();

        var itemType = GetString(item, "item_type") ?? GetString(item, "type");
        var id = GetString(item, "id");

        switch (itemType)
        {
            case "agent_message" when completed:
            {
                var message = GetString(item, "text");
                if (string.IsNullOrEmpty(message) || !MarkMessageEmitted(id))
                    return Array.Empty<AgentStreamEvent>();
                _assistantText.Append(message);
                return new AgentStreamEvent[] { new AssistantTextDeltaEvent(id, message) };
            }
            case "reasoning" when completed:
            {
                var thinking = GetString(item, "text");
                if (string.IsNullOrEmpty(thinking) || !MarkMessageEmitted(id))
                    return Array.Empty<AgentStreamEvent>();
                return new AgentStreamEvent[] { new AssistantThinkingDeltaEvent(id, thinking) };
            }
            case "command_execution":
            case "file_change":
            case "mcp_tool_call":
            case "web_search":
                return completed
                    ? CompleteTool(item, id, itemType)
                    : StartTool(item, id, itemType!);
            default:
                return Array.Empty<AgentStreamEvent>();
        }
    }

    private IEnumerable<AgentStreamEvent> StartTool(JsonElement item, string? id, string itemType)
    {
        if (string.IsNullOrEmpty(id) || !_startedToolCalls.Add(id))
            return Array.Empty<AgentStreamEvent>();

        return new AgentStreamEvent[] { BuildToolStarted(item, id, itemType) };
    }

    private IEnumerable<AgentStreamEvent> CompleteTool(JsonElement item, string? id, string? itemType)
    {
        if (string.IsNullOrEmpty(id))
            return Array.Empty<AgentStreamEvent>();

        var events = new List<AgentStreamEvent>();
        if (_startedToolCalls.Add(id))
            events.Add(BuildToolStarted(item, id, itemType ?? "tool"));

        var content = GetString(item, "aggregated_output")
            ?? GetString(item, "output")
            ?? GetString(item, "result");
        events.Add(new ToolCallCompletedEvent(id, MapToolStatus(item), BuildSummary(content), content));
        return events;
    }

    private static ToolCallStartedEvent BuildToolStarted(JsonElement item, string id, string itemType)
    {
        var (name, kind) = itemType switch
        {
            "command_execution" => (GetString(item, "command") ?? "command", ToolCallKind.Run),
            "file_change" => ("file_change", ToolCallKind.Edit),
            "web_search" => (GetString(item, "query") ?? "web_search", ToolCallKind.Search),
            "mcp_tool_call" => (BuildMcpToolName(item), ToolCallKind.Other),
            _ => (itemType, ToolCallKind.Other),
        };

        return new ToolCallStartedEvent(id, name, item.GetRawText(), kind);
    }

    private static ToolCallStatus MapToolStatus(JsonElement item)
    {
        var status = GetString(item, "status");
        if (string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "error", StringComparison.OrdinalIgnoreCase))
            return ToolCallStatus.Failed;

        var exitCode = GetInt(item, "exit_code");
        return exitCode is { } code && code != 0
            ? ToolCallStatus.Failed
            : ToolCallStatus.Completed;
    }

    private static string BuildMcpToolName(JsonElement item)
    {
        var server = GetString(item, "server");
        var tool = GetString(item, "tool") ?? GetString(item, "name");
        if (!string.IsNullOrEmpty(server) && !string.IsNullOrEmpty(tool))
            return $"{server}.{tool}";
        return tool ?? server ?? "mcp_tool_call";
    }

    private static IEnumerable<AgentStreamEvent> HandleUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
            return Array.Empty<AgentStreamEvent>();

        return new AgentStreamEvent[]
        {
            new UsageReportedEvent(
                InputTokens: GetInt(usage, "input_tokens"),
                OutputTokens: GetInt(usage, "output_tokens")),
        };
    }

    private IEnumerable<AgentStreamEvent> StartRun(string? sessionId)
    {
        if (_runStarted)
            return Array.Empty<AgentStreamEvent>();

        _runStarted = true;
        return new AgentStreamEvent[]
        {
            new RunStartedEvent(sessionId, Model: null, CliAgentKind.Codex),
            new RunStatusEvent(AgentRunStatus.Initializing),
        };
    }

    private bool MarkMessageEmitted(string? id)
    {
        if (string.IsNullOrEmpty(id))
            return true;
        return _emittedMessages.Add(id);
    }

    private string? FinalTextOrNull() => _assistantText.Length > 0 ? _assistantText.ToString() : null;
}
