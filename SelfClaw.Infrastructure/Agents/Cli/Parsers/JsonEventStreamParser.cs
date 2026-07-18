using System.Text;
using System.Text.Json;
using SelfClaw.Core.Runtime.Agent;

namespace SelfClaw.Infrastructure.Agents.Cli.Parsers;

/// <summary>
/// Parses the newline-delimited JSON event stream shared by Codex (<c>exec --json</c>) and OpenCode
/// (<c>run --format json</c>) into <see cref="AgentStreamEvent"/>s (plan.md 阶段 7, T7.1; mirroring Open
/// Design's <c>json-event-stream.ts</c>). A single instance handles one agent kind, selected at
/// construction; the two agents share the base parser's JSON envelope but differ in the per-line dispatch
/// (<see cref="HandleCodexEvent"/> vs <see cref="HandleOpenCodeEvent"/>).
/// <para>
/// Both agents mint their own session id and report it mid-stream
/// (<see cref="Definitions.ResumeStrategy.CapturedFromStream"/>): Codex via <c>thread.started.thread_id</c>,
/// OpenCode via <c>step_start.sessionID</c>. The id rides out on <see cref="RunStartedEvent.SessionId"/> so
/// the runtime can persist it for the next turn (plan.md §6).
/// </para>
/// <para>
/// Neither agent streams token-level text deltas the way Claude does under
/// <c>--include-partial-messages</c>; each emits a full message/part on completion, which we surface as a
/// single <see cref="AssistantTextDeltaEvent"/> / <see cref="AssistantThinkingDeltaEvent"/>. Lines that are
/// not valid JSON are surfaced verbatim as <see cref="RawOutputEvent"/> by the base parser (T3.3);
/// valid-but-unrecognised event types are ignored rather than spammed.
/// </para>
/// </summary>
public sealed class JsonEventStreamParser : CliStreamParser
{
    private readonly CliAgentKind _kind;

    private bool _runStarted;

    // Codex/OpenCode item ids already surfaced as ToolCallStartedEvent (deduped across started/updated).
    private readonly HashSet<string> _startedToolCalls = new(StringComparer.Ordinal);

    // Item ids whose assistant text/thinking we have already emitted, so updated+completed of the same
    // item don't render twice.
    private readonly HashSet<string> _emittedMessages = new(StringComparer.Ordinal);

    // Accumulated assistant text, used as the final text fallback when no terminal event carries one.
    private readonly StringBuilder _assistantText = new();

    /// <param name="kind">
    /// The agent whose stream this parses; must be <see cref="CliAgentKind.Codex"/> or
    /// <see cref="CliAgentKind.OpenCode"/>.
    /// </param>
    public JsonEventStreamParser(CliAgentKind kind)
    {
        if (kind is not (CliAgentKind.Codex or CliAgentKind.OpenCode))
            throw new ArgumentOutOfRangeException(
                nameof(kind), kind, "JsonEventStreamParser only handles Codex and OpenCode streams.");
        _kind = kind;
    }

    protected override IEnumerable<AgentStreamEvent> HandleObject(JsonElement root)
        => _kind switch
        {
            CliAgentKind.Codex => HandleCodexEvent(root),
            CliAgentKind.OpenCode => HandleOpenCodeEvent(root),
            _ => Array.Empty<AgentStreamEvent>(),
        };

    // ---- Codex (exec --json) -------------------------------------------------------------------

    /// <summary>
    /// Handles one Codex <c>exec --json</c> event. Codex frames its output as <c>thread.*</c> /
    /// <c>turn.*</c> lifecycle events and <c>item.*</c> events that wrap an <c>item</c> object whose
    /// <c>item_type</c> (alias <c>type</c>) selects the payload: <c>agent_message</c> / <c>reasoning</c>
    /// carry assistant text; <c>command_execution</c> / <c>file_change</c> / <c>mcp_tool_call</c> /
    /// <c>web_search</c> are tool calls (plan.md §5.2).
    /// </summary>
    private IEnumerable<AgentStreamEvent> HandleCodexEvent(JsonElement root)
    {
        switch (GetString(root, "type"))
        {
            case "thread.started":
                return StartRun(GetString(root, "thread_id"), AgentRunStatus.Initializing);

            case "turn.started":
                return new AgentStreamEvent[] { new RunStatusEvent(AgentRunStatus.Running) };

            // Tool calls surface on item.started; assistant text/results on item.completed. item.updated
            // is intentionally ignored — Codex repeats the full payload on completion, so emitting on
            // updated would double-render.
            case "item.started":
                return HandleCodexItem(root, completed: false);

            case "item.completed":
                return HandleCodexItem(root, completed: true);

            case "turn.completed":
                return HandleCodexUsage(root);

            case "error":
            {
                var message = GetString(root, "message") ?? "The Codex agent reported an error.";
                return new AgentStreamEvent[]
                {
                    new RunCompletedEvent(RunCompletionStatus.Failed, FinalText: FinalTextOrNull(), ErrorMessage: message),
                };
            }

            default:
                return Array.Empty<AgentStreamEvent>();
        }
    }

    private IEnumerable<AgentStreamEvent> HandleCodexItem(JsonElement root, bool completed)
    {
        if (!root.TryGetProperty("item", out var item) || item.ValueKind != JsonValueKind.Object)
            return Array.Empty<AgentStreamEvent>();

        // Codex has used both `item_type` and `type` across versions; accept either.
        var itemType = GetString(item, "item_type") ?? GetString(item, "type");
        var id = GetString(item, "id");

        switch (itemType)
        {
            case "agent_message" when completed:
            {
                var text = GetString(item, "text");
                if (string.IsNullOrEmpty(text) || !MarkMessageEmitted(id))
                    return Array.Empty<AgentStreamEvent>();
                _assistantText.Append(text);
                return new AgentStreamEvent[] { new AssistantTextDeltaEvent(id, text) };
            }

            case "reasoning" when completed:
            {
                var text = GetString(item, "text");
                if (string.IsNullOrEmpty(text) || !MarkMessageEmitted(id))
                    return Array.Empty<AgentStreamEvent>();
                return new AgentStreamEvent[] { new AssistantThinkingDeltaEvent(id, text) };
            }

            case "command_execution":
            case "file_change":
            case "mcp_tool_call":
            case "web_search":
                return completed
                    ? CompleteCodexTool(item, id, itemType)
                    : StartCodexTool(item, id, itemType!);

            default:
                return Array.Empty<AgentStreamEvent>();
        }
    }

    private IEnumerable<AgentStreamEvent> StartCodexTool(JsonElement item, string? id, string itemType)
    {
        if (string.IsNullOrEmpty(id) || !_startedToolCalls.Add(id))
            return Array.Empty<AgentStreamEvent>();

        return new AgentStreamEvent[] { BuildCodexToolStarted(item, id, itemType) };
    }

    private IEnumerable<AgentStreamEvent> CompleteCodexTool(JsonElement item, string? id, string? itemType)
    {
        if (string.IsNullOrEmpty(id))
            return Array.Empty<AgentStreamEvent>();

        // A tool may complete in a single item.completed without a preceding item.started; in that case
        // synthesize the started event first so the transcript has a matching anchor.
        var events = new List<AgentStreamEvent>();
        if (_startedToolCalls.Add(id))
            events.Add(BuildCodexToolStarted(item, id, itemType ?? "tool"));

        var status = MapCodexToolStatus(item);
        var content = GetString(item, "aggregated_output")
            ?? GetString(item, "output")
            ?? GetString(item, "result");

        events.Add(new ToolCallCompletedEvent(id, status, BuildSummary(content), content));
        return events;
    }

    private static ToolCallStartedEvent BuildCodexToolStarted(JsonElement item, string id, string itemType)
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

    private static ToolCallStatus MapCodexToolStatus(JsonElement item)
    {
        // Prefer an explicit status, then fall back to a non-zero exit code for command_execution.
        var status = GetString(item, "status");
        if (string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "error", StringComparison.OrdinalIgnoreCase))
            return ToolCallStatus.Failed;

        var exitCode = GetInt(item, "exit_code");
        if (exitCode is { } code && code != 0)
            return ToolCallStatus.Failed;

        return ToolCallStatus.Completed;
    }

    private static string BuildMcpToolName(JsonElement item)
    {
        var server = GetString(item, "server");
        var tool = GetString(item, "tool") ?? GetString(item, "name");
        if (!string.IsNullOrEmpty(server) && !string.IsNullOrEmpty(tool))
            return $"{server}.{tool}";
        return tool ?? server ?? "mcp_tool_call";
    }

    private IEnumerable<AgentStreamEvent> HandleCodexUsage(JsonElement root)
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

    // ---- OpenCode (run --format json) ----------------------------------------------------------

    /// <summary>
    /// Handles one OpenCode <c>run --format json</c> event. OpenCode emits <c>step_start</c> /
    /// <c>step_finish</c> lifecycle markers and message <c>part</c>s (<c>text</c> / <c>reasoning</c> /
    /// <c>tool</c>). Content may sit directly on the event or nested under a <c>part</c> object, so we
    /// look in both places (plan.md §5.3).
    /// </summary>
    private IEnumerable<AgentStreamEvent> HandleOpenCodeEvent(JsonElement root)
    {
        var payload = root.TryGetProperty("part", out var part) && part.ValueKind == JsonValueKind.Object
            ? part
            : root;

        switch (GetString(root, "type"))
        {
            case "step_start":
                return StartRun(GetString(root, "sessionID") ?? GetString(payload, "sessionID"), AgentRunStatus.Running);

            case "text":
            {
                var text = GetString(payload, "text") ?? GetString(root, "text");
                if (string.IsNullOrEmpty(text))
                    return Array.Empty<AgentStreamEvent>();
                _assistantText.Append(text);
                return new AgentStreamEvent[] { new AssistantTextDeltaEvent(GetString(payload, "id"), text) };
            }

            case "reasoning":
            {
                var text = GetString(payload, "text") ?? GetString(root, "text");
                if (string.IsNullOrEmpty(text))
                    return Array.Empty<AgentStreamEvent>();
                return new AgentStreamEvent[] { new AssistantThinkingDeltaEvent(GetString(payload, "id"), text) };
            }

            // OpenCode ≤1.16 framed tool calls with a top-level type of "tool"; 1.17+ uses
            // "tool_use" (the nested part keeps `type: "tool"`). Accept both.
            case "tool":
            case "tool_use":
                return HandleOpenCodeTool(payload);

            case "step_finish":
                return HandleOpenCodeUsage(root, payload);

            default:
                return Array.Empty<AgentStreamEvent>();
        }
    }

    private IEnumerable<AgentStreamEvent> HandleOpenCodeTool(JsonElement part)
    {
        var id = GetString(part, "callID") ?? GetString(part, "id");
        if (string.IsNullOrEmpty(id))
            return Array.Empty<AgentStreamEvent>();

        var toolName = GetString(part, "tool") ?? GetString(part, "name") ?? "tool";

        // The tool's lifecycle lives under `state.status`: pending/running while in flight,
        // completed/error once done.
        var status = part.TryGetProperty("state", out var state) && state.ValueKind == JsonValueKind.Object
            ? GetString(state, "status")
            : null;

        var events = new List<AgentStreamEvent>();

        if (_startedToolCalls.Add(id))
        {
            var argumentsJson = OpenCodeToolArguments(part, state: part.TryGetProperty("state", out var s) ? s : default);
            events.Add(new ToolCallStartedEvent(id, toolName, argumentsJson, MapOpenCodeToolKind(toolName)));
        }

        if (IsTerminalOpenCodeStatus(status))
        {
            var content = part.TryGetProperty("state", out var st) && st.ValueKind == JsonValueKind.Object
                ? GetString(st, "output") ?? GetString(st, "result")
                : null;
            var completion = string.Equals(status, "error", StringComparison.OrdinalIgnoreCase)
                ? ToolCallStatus.Failed
                : ToolCallStatus.Completed;
            events.Add(new ToolCallCompletedEvent(id, completion, BuildSummary(content), content));
        }

        return events;
    }

    private static string OpenCodeToolArguments(JsonElement part, JsonElement state)
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

    private static bool IsTerminalOpenCodeStatus(string? status) =>
        string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase)
        || string.Equals(status, "error", StringComparison.OrdinalIgnoreCase);

    private static ToolCallKind MapOpenCodeToolKind(string name) => name.ToLowerInvariant() switch
    {
        "read" => ToolCallKind.Read,
        "write" or "edit" or "patch" => ToolCallKind.Edit,
        "bash" or "shell" => ToolCallKind.Run,
        "grep" or "webfetch" or "websearch" => ToolCallKind.Search,
        "glob" or "list" or "ls" => ToolCallKind.List,
        _ => ToolCallKind.Other,
    };

    private IEnumerable<AgentStreamEvent> HandleOpenCodeUsage(JsonElement root, JsonElement payload)
    {
        // OpenCode reports tokens either as a `tokens` object ({input, output}) or a `usage` object.
        var source = FindUsageObject(root) ?? FindUsageObject(payload);
        if (source is not { } usage)
            return Array.Empty<AgentStreamEvent>();

        var input = GetInt(usage, "input") ?? GetInt(usage, "input_tokens");
        var output = GetInt(usage, "output") ?? GetInt(usage, "output_tokens");
        if (input is null && output is null)
            return Array.Empty<AgentStreamEvent>();

        return new AgentStreamEvent[] { new UsageReportedEvent(input, output) };
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

    // ---- shared helpers ------------------------------------------------------------------------

    private IEnumerable<AgentStreamEvent> StartRun(string? sessionId, AgentRunStatus status)
    {
        if (_runStarted)
            return Array.Empty<AgentStreamEvent>();

        _runStarted = true;
        return new AgentStreamEvent[]
        {
            new RunStartedEvent(SessionId: sessionId, Model: null, AgentKind: _kind),
            new RunStatusEvent(status),
        };
    }

    private bool MarkMessageEmitted(string? id)
    {
        // Items without an id can't be deduped; emit them rather than dropping content.
        if (string.IsNullOrEmpty(id))
            return true;
        return _emittedMessages.Add(id);
    }

    private string? FinalTextOrNull() => _assistantText.Length > 0 ? _assistantText.ToString() : null;
}
