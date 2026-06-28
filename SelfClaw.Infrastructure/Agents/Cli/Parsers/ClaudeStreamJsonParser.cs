using System.Text;
using System.Text.Json;
using SelfClaw.Core.Runtime.Agent;

namespace SelfClaw.Infrastructure.Agents.Cli.Parsers;

/// <summary>
/// Parses Claude Code's <c>stream-json</c> output (emitted under
/// <c>-p --output-format stream-json --verbose</c>) into <see cref="AgentStreamEvent"/>s.
/// <para>
/// Claude emits newline-delimited JSON objects discriminated by a top-level <c>type</c>:
/// <list type="bullet">
///   <item><c>system</c>/<c>init</c> — session id + model (→ <see cref="RunStartedEvent"/>).</item>
///   <item><c>assistant</c> — a full assistant message whose <c>content</c> array holds
///         <c>text</c> / <c>thinking</c> / <c>tool_use</c> blocks.</item>
///   <item><c>user</c> — carries <c>tool_result</c> blocks (→ <see cref="ToolCallCompletedEvent"/>).</item>
///   <item><c>result</c> — final text + usage (→ <see cref="UsageReportedEvent"/> + <see cref="RunCompletedEvent"/>).</item>
///   <item><c>stream_event</c> — present only with <c>--include-partial-messages</c>; wraps the raw
///         Anthropic streaming events (<c>content_block_delta</c> / <c>thinking_delta</c>) for
///         token-level streaming.</item>
/// </list>
/// Both delivery modes are handled: when partial streaming is on, text/thinking arrive as deltas and
/// the matching blocks in the full <c>assistant</c> message are suppressed to avoid duplication; when
/// it is off, the full message blocks are emitted as single deltas. Lines that are not valid JSON are
/// surfaced verbatim as <see cref="RawOutputEvent"/> (plan.md 阶段 3, T3.3).
/// </para>
/// </summary>
public sealed class ClaudeStreamJsonParser : IAgentStreamParser
{
    private readonly StringBuilder _buffer = new();

    // Captured from system/init; replayed on RunStartedEvent.
    private bool _runStarted;

    // The id of the assistant message currently being streamed via partial events, used to correlate
    // partial deltas with the full assistant message so we don't emit the same text twice.
    private string? _currentMessageId;

    // messageId → set of content-block indices already streamed as deltas (text/thinking).
    private readonly Dictionary<string, HashSet<int>> _streamedBlocks = new(StringComparer.Ordinal);

    // tool_use ids already surfaced as ToolCallStartedEvent (deduped across full/partial paths).
    private readonly HashSet<string> _startedToolCalls = new(StringComparer.Ordinal);

    // Accumulated assistant text, used as the final text fallback if `result` carries none.
    private readonly StringBuilder _assistantText = new();

    public IEnumerable<AgentStreamEvent> Feed(string chunk)
    {
        if (string.IsNullOrEmpty(chunk))
            return Array.Empty<AgentStreamEvent>();

        _buffer.Append(chunk);
        return DrainCompleteLines();
    }

    public IEnumerable<AgentStreamEvent> Flush()
    {
        if (_buffer.Length == 0)
            return Array.Empty<AgentStreamEvent>();

        // Treat whatever remains (no trailing newline) as a final line.
        var remainder = _buffer.ToString();
        _buffer.Clear();
        return ParseLine(remainder);
    }

    private IEnumerable<AgentStreamEvent> DrainCompleteLines()
    {
        var events = new List<AgentStreamEvent>();

        while (true)
        {
            var text = _buffer.ToString();
            var newline = text.IndexOf('\n');
            if (newline < 0)
                break;

            var line = text[..newline];
            _buffer.Remove(0, newline + 1);
            events.AddRange(ParseLine(line));
        }

        return events;
    }

    private IEnumerable<AgentStreamEvent> ParseLine(string rawLine)
    {
        var line = rawLine.Trim('\r', ' ', '\t');
        if (line.Length == 0)
            return Array.Empty<AgentStreamEvent>();

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            // T3.3: any line we cannot parse is passed through for diagnostics.
            return new AgentStreamEvent[] { new RawOutputEvent(line) };
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return new AgentStreamEvent[] { new RawOutputEvent(line) };

            var type = GetString(root, "type");
            return type switch
            {
                "system" => HandleSystem(root),
                "stream_event" => HandleStreamEvent(root),
                "assistant" => HandleAssistant(root),
                "user" => HandleUser(root),
                "result" => HandleResult(root),
                // Unknown-but-valid types (e.g. control acks) are ignored rather than spamming raw.
                _ => Array.Empty<AgentStreamEvent>(),
            };
        }
    }

    private IEnumerable<AgentStreamEvent> HandleSystem(JsonElement root)
    {
        if (GetString(root, "subtype") != "init")
            return Array.Empty<AgentStreamEvent>();

        var events = new List<AgentStreamEvent>();
        if (!_runStarted)
        {
            _runStarted = true;
            events.Add(new RunStartedEvent(
                SessionId: GetString(root, "session_id"),
                Model: GetString(root, "model"),
                AgentKind: CliAgentKind.Claude));
            events.Add(new RunStatusEvent(AgentRunStatus.Initializing));
        }

        return events;
    }

    /// <summary>
    /// Handles the partial-streaming wrapper (<c>--include-partial-messages</c>). The inner
    /// <c>event</c> is a raw Anthropic streaming event; we care about message boundaries and
    /// text/thinking deltas.
    /// </summary>
    private IEnumerable<AgentStreamEvent> HandleStreamEvent(JsonElement root)
    {
        if (!root.TryGetProperty("event", out var ev) || ev.ValueKind != JsonValueKind.Object)
            return Array.Empty<AgentStreamEvent>();

        switch (GetString(ev, "type"))
        {
            case "message_start":
                _currentMessageId = ev.TryGetProperty("message", out var msg)
                    ? GetString(msg, "id")
                    : null;
                return Array.Empty<AgentStreamEvent>();

            case "content_block_delta":
                return HandleContentBlockDelta(ev);

            default:
                return Array.Empty<AgentStreamEvent>();
        }
    }

    private IEnumerable<AgentStreamEvent> HandleContentBlockDelta(JsonElement ev)
    {
        if (!ev.TryGetProperty("delta", out var delta) || delta.ValueKind != JsonValueKind.Object)
            return Array.Empty<AgentStreamEvent>();

        var index = GetInt(ev, "index") ?? -1;
        var blockId = BuildBlockId(_currentMessageId, index);

        switch (GetString(delta, "type"))
        {
            case "text_delta":
            {
                var text = GetString(delta, "text");
                if (string.IsNullOrEmpty(text))
                    return Array.Empty<AgentStreamEvent>();
                MarkStreamed(_currentMessageId, index);
                _assistantText.Append(text);
                return new AgentStreamEvent[] { new AssistantTextDeltaEvent(blockId, text) };
            }

            case "thinking_delta":
            {
                var thinking = GetString(delta, "thinking");
                if (string.IsNullOrEmpty(thinking))
                    return Array.Empty<AgentStreamEvent>();
                MarkStreamed(_currentMessageId, index);
                return new AgentStreamEvent[] { new AssistantThinkingDeltaEvent(blockId, thinking) };
            }

            default:
                // input_json_delta / signature_delta — tool input/signatures arrive complete in the
                // full assistant message, so we don't reconstruct them here.
                return Array.Empty<AgentStreamEvent>();
        }
    }

    /// <summary>
    /// Handles a complete assistant message. Text/thinking blocks already streamed as partial deltas
    /// are skipped; everything else is emitted (full text when partial streaming is off, tool calls
    /// always).
    /// </summary>
    private IEnumerable<AgentStreamEvent> HandleAssistant(JsonElement root)
    {
        if (!root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object)
            return Array.Empty<AgentStreamEvent>();

        var messageId = GetString(message, "id");
        if (!message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            return Array.Empty<AgentStreamEvent>();

        var streamed = messageId is not null && _streamedBlocks.TryGetValue(messageId, out var set)
            ? set
            : null;

        var events = new List<AgentStreamEvent>();
        var index = 0;
        foreach (var block in content.EnumerateArray())
        {
            var blockType = GetString(block, "type");
            var alreadyStreamed = streamed?.Contains(index) == true;

            switch (blockType)
            {
                case "text" when !alreadyStreamed:
                {
                    var text = GetString(block, "text");
                    if (!string.IsNullOrEmpty(text))
                    {
                        _assistantText.Append(text);
                        events.Add(new AssistantTextDeltaEvent(BuildBlockId(messageId, index), text));
                    }
                    break;
                }

                case "thinking" when !alreadyStreamed:
                {
                    var thinking = GetString(block, "thinking");
                    if (!string.IsNullOrEmpty(thinking))
                        events.Add(new AssistantThinkingDeltaEvent(BuildBlockId(messageId, index), thinking));
                    break;
                }

                case "tool_use":
                {
                    var toolCall = BuildToolCallStarted(block);
                    if (toolCall is not null)
                        events.Add(toolCall);
                    break;
                }
            }

            index++;
        }

        return events;
    }

    private ToolCallStartedEvent? BuildToolCallStarted(JsonElement block)
    {
        var id = GetString(block, "id");
        var name = GetString(block, "name");
        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(name))
            return null;
        if (!_startedToolCalls.Add(id))
            return null;

        var argumentsJson = block.TryGetProperty("input", out var input)
            ? input.GetRawText()
            : "{}";

        return new ToolCallStartedEvent(id, name, argumentsJson, MapToolKind(name));
    }

    /// <summary>Handles a user message, surfacing any <c>tool_result</c> blocks as completions.</summary>
    private IEnumerable<AgentStreamEvent> HandleUser(JsonElement root)
    {
        if (!root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object)
            return Array.Empty<AgentStreamEvent>();
        if (!message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            return Array.Empty<AgentStreamEvent>();

        var events = new List<AgentStreamEvent>();
        foreach (var block in content.EnumerateArray())
        {
            if (GetString(block, "type") != "tool_result")
                continue;

            var toolUseId = GetString(block, "tool_use_id");
            if (string.IsNullOrEmpty(toolUseId))
                continue;

            var isError = block.TryGetProperty("is_error", out var err)
                && err.ValueKind == JsonValueKind.True;
            var resultContent = ExtractToolResultContent(block);

            events.Add(new ToolCallCompletedEvent(
                toolUseId,
                isError ? ToolCallStatus.Failed : ToolCallStatus.Completed,
                ResultSummary: BuildSummary(resultContent),
                ResultContent: resultContent));
        }

        return events;
    }

    private IEnumerable<AgentStreamEvent> HandleResult(JsonElement root)
    {
        var events = new List<AgentStreamEvent>();

        if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
        {
            events.Add(new UsageReportedEvent(
                InputTokens: GetInt(usage, "input_tokens"),
                OutputTokens: GetInt(usage, "output_tokens")));
        }

        var subtype = GetString(root, "subtype");
        var isError = root.TryGetProperty("is_error", out var err) && err.ValueKind == JsonValueKind.True;
        var status = (subtype, isError) switch
        {
            ("success", false) => RunCompletionStatus.Succeeded,
            (_, true) => RunCompletionStatus.Failed,
            ("error_max_turns", _) => RunCompletionStatus.Failed,
            ("error_during_execution", _) => RunCompletionStatus.Failed,
            _ => RunCompletionStatus.Succeeded,
        };

        var finalText = GetString(root, "result");
        if (string.IsNullOrEmpty(finalText) && _assistantText.Length > 0)
            finalText = _assistantText.ToString();

        var errorMessage = status == RunCompletionStatus.Failed
            ? GetString(root, "error") ?? subtype
            : null;

        events.Add(new RunCompletedEvent(status, finalText, errorMessage));
        return events;
    }

    /// <summary>Extracts tool_result content, which may be a string or an array of content blocks.</summary>
    private static string? ExtractToolResultContent(JsonElement block)
    {
        if (!block.TryGetProperty("content", out var content))
            return null;

        switch (content.ValueKind)
        {
            case JsonValueKind.String:
                return content.GetString();

            case JsonValueKind.Array:
            {
                var sb = new StringBuilder();
                foreach (var item in content.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Object && GetString(item, "type") == "text")
                    {
                        if (sb.Length > 0)
                            sb.Append('\n');
                        sb.Append(GetString(item, "text"));
                    }
                }
                return sb.Length > 0 ? sb.ToString() : content.GetRawText();
            }

            default:
                return content.GetRawText();
        }
    }

    private static string? BuildSummary(string? content)
    {
        if (string.IsNullOrEmpty(content))
            return null;

        const int maxLength = 120;
        var firstLine = content.AsSpan();
        var newline = firstLine.IndexOfAny('\r', '\n');
        if (newline >= 0)
            firstLine = firstLine[..newline];

        var summary = firstLine.Trim().ToString();
        return summary.Length > maxLength ? summary[..maxLength] + "…" : summary;
    }

    /// <summary>Maps a Claude Code tool name onto a coarse <see cref="ToolCallKind"/> for the transcript.</summary>
    private static ToolCallKind MapToolKind(string name) => name switch
    {
        "Read" or "NotebookRead" => ToolCallKind.Read,
        "Write" or "Edit" or "MultiEdit" or "NotebookEdit" => ToolCallKind.Edit,
        "Bash" or "BashOutput" or "KillBash" or "KillShell" => ToolCallKind.Run,
        "Grep" or "WebSearch" or "WebFetch" => ToolCallKind.Search,
        "Glob" or "LS" or "ListMcpResources" => ToolCallKind.List,
        _ => ToolCallKind.Other,
    };

    private void MarkStreamed(string? messageId, int index)
    {
        if (messageId is null || index < 0)
            return;
        if (!_streamedBlocks.TryGetValue(messageId, out var set))
        {
            set = new HashSet<int>();
            _streamedBlocks[messageId] = set;
        }
        set.Add(index);
    }

    private static string? BuildBlockId(string? messageId, int index)
    {
        if (index < 0)
            return messageId;
        return messageId is null ? index.ToString() : $"{messageId}:{index}";
    }

    private static string? GetString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? GetInt(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out var number)
            ? number
            : null;
}
