using System.Globalization;
using System.Text.Json;
using SelfClaw.Core.Models;

namespace SelfClaw.Desktop.Services;

internal static class TranscriptToolRunPresenter
{
    private static readonly JsonSerializerOptions IndentedJsonOptions = new() { WriteIndented = true };

    public static Dictionary<Guid, IReadOnlyList<ToolRunPlacement>> BuildToolRunsByMessageId(
        IReadOnlyList<MessageRecord> messages,
        IReadOnlyList<ToolExecutionRecord> toolRuns,
        IReadOnlyDictionary<Guid, ToolRunAnchor> toolRunAnchors)
    {
        var result = new Dictionary<Guid, List<ToolRunPlacement>>();
        var orderedMessages = messages
            .OrderBy(item => item.CreatedAtUtc)
            .ToArray();

        if (orderedMessages.Length == 0 || toolRuns.Count == 0)
        {
            return [];
        }

        Guid? currentAssistantMessageId = null;
        var messageIndex = 0;

        foreach (var toolRun in toolRuns.OrderBy(item => item.CreatedAtUtc))
        {
            if (toolRunAnchors.TryGetValue(toolRun.Id, out var anchor))
            {
                AddPlacement(anchor.MessageId, new ToolRunPlacement(toolRun, anchor.AfterSegmentIndex));
                continue;
            }

            if (toolRun.MessageId is Guid anchoredMessageId && toolRun.AfterSegmentIndex is int afterSegmentIndex)
            {
                AddPlacement(anchoredMessageId, new ToolRunPlacement(toolRun, afterSegmentIndex));
                continue;
            }

            while (messageIndex < orderedMessages.Length && orderedMessages[messageIndex].CreatedAtUtc <= toolRun.CreatedAtUtc)
            {
                var message = orderedMessages[messageIndex++];
                switch (message.Role)
                {
                    case MessageRole.Assistant:
                        currentAssistantMessageId = message.Id;
                        break;
                    case MessageRole.User:
                        currentAssistantMessageId = null;
                        break;
                }
            }

            if (currentAssistantMessageId is not Guid messageId)
            {
                continue;
            }

            AddPlacement(messageId, new ToolRunPlacement(toolRun, null));
        }

        return result.ToDictionary(item => item.Key, item => (IReadOnlyList<ToolRunPlacement>)item.Value);

        void AddPlacement(Guid messageId, ToolRunPlacement placement)
        {
            if (!result.TryGetValue(messageId, out var messageToolRuns))
            {
                messageToolRuns = [];
                result[messageId] = messageToolRuns;
            }

            messageToolRuns.Add(placement);
        }
    }

    public static void InsertToolSegments(List<TranscriptRenderSegment> renderSegments, IReadOnlyList<ToolRunPlacement> toolRuns)
    {
        if (toolRuns.Count == 0)
        {
            return;
        }

        var baseSegments = renderSegments.ToArray();
        if (baseSegments.Length == 0)
        {
            renderSegments.AddRange(toolRuns.Select(item => BuildToolSegment(item.Record)));
            return;
        }

        var candidateIndexes = Enumerable.Range(0, baseSegments.Length)
            .Where(index => baseSegments[index].Kind is "thinking" or "content")
            .ToArray();

        var buckets = new Dictionary<int, List<TranscriptRenderSegment>>();
        var fallbackCursor = 0;

        foreach (var placement in toolRuns.OrderBy(item => item.Record.CreatedAtUtc))
        {
            var afterSegmentIndex = ResolveInsertionIndex(placement.AfterSegmentIndex, candidateIndexes, fallbackCursor);
            if (!placement.AfterSegmentIndex.HasValue && candidateIndexes.Length > 0 && fallbackCursor < candidateIndexes.Length - 1)
            {
                fallbackCursor++;
            }

            if (!buckets.TryGetValue(afterSegmentIndex, out var bucket))
            {
                bucket = [];
                buckets[afterSegmentIndex] = bucket;
            }

            bucket.Add(BuildToolSegment(placement.Record));
        }

        renderSegments.Clear();
        if (buckets.TryGetValue(-1, out var leadingSegments))
        {
            renderSegments.AddRange(leadingSegments);
        }

        for (var index = 0; index < baseSegments.Length; index++)
        {
            renderSegments.Add(baseSegments[index]);

            if (buckets.TryGetValue(index, out var toolSegments))
            {
                renderSegments.AddRange(toolSegments);
            }
        }
    }

    public static TranscriptRenderSegment BuildToolSegment(ToolExecutionRecord toolRun)
        => new(
            "tool",
            string.Empty,
            toolRun.Status is ToolExecutionStatus.Running or ToolExecutionStatus.AwaitingApproval,
            BuildInlineToolSummary(toolRun),
            toolRun.Status.ToString().ToLowerInvariant(),
            toolRun.Id.ToString("D"),
            FormatInlineToolDuration(toolRun),
            BuildToolDetailTitle(toolRun),
            BuildToolDetailText(toolRun));

    private static int ResolveInsertionIndex(int? anchoredIndex, IReadOnlyList<int> candidateIndexes, int fallbackCursor)
    {
        if (candidateIndexes.Count == 0)
        {
            return -1;
        }

        if (anchoredIndex.HasValue)
        {
            if (anchoredIndex.Value < 0)
            {
                return -1;
            }

            return candidateIndexes
                .Where(index => index <= anchoredIndex.Value)
                .DefaultIfEmpty(candidateIndexes[0])
                .Last();
        }

        return candidateIndexes[Math.Min(fallbackCursor, candidateIndexes.Count - 1)];
    }

    private static string BuildInlineToolSummary(ToolExecutionRecord toolRun)
    {
        using var arguments = ParseJsonObject(toolRun.ArgumentsJson);

        return toolRun.ToolName switch
        {
            "read_workspace_file" => $"Read {ReadArgument(arguments, "relativePath", "file")}",
            "write_workspace_file" => $"{ResolveWriteVerb(toolRun)} {ReadArgument(arguments, "relativePath", "file")}",
            "run_shell_command" => $"Run {ReadArgument(arguments, "command", "command", maxLength: 44)}",
            "list_workspace_files" => BuildListSummary(arguments),
            "search_workspace_text" => $"Search {Quote(ReadArgument(arguments, "query", "text", maxLength: 28))}",
            _ => HumanizeToolName(toolRun.ToolName)
        };
    }

    private static string? FormatInlineToolDuration(ToolExecutionRecord toolRun)
    {
        if (toolRun.DurationMs is not double durationMs || durationMs < 1)
        {
            return null;
        }

        if (durationMs < 1_000)
        {
            return $"{Math.Round(durationMs).ToString(CultureInfo.InvariantCulture)}ms";
        }

        var seconds = durationMs / 1_000d;
        if (seconds < 10)
        {
            var roundedSeconds = Math.Round(seconds, 1);
            return roundedSeconds % 1 == 0
                ? $"{Math.Round(roundedSeconds).ToString(CultureInfo.InvariantCulture)}s"
                : $"{roundedSeconds.ToString("0.0", CultureInfo.InvariantCulture)}s";
        }

        if (seconds < 60)
        {
            return $"{Math.Round(seconds).ToString(CultureInfo.InvariantCulture)}s";
        }

        var minutes = Math.Floor(seconds / 60d);
        var remainingSeconds = Math.Round(seconds % 60d);
        return remainingSeconds <= 0
            ? $"{minutes.ToString(CultureInfo.InvariantCulture)}m"
            : $"{minutes.ToString(CultureInfo.InvariantCulture)}m {remainingSeconds.ToString(CultureInfo.InvariantCulture)}s";
    }

    private static string PrettyPrintJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return "{}";
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            return JsonSerializer.Serialize(document.RootElement, IndentedJsonOptions);
        }
        catch
        {
            return json;
        }
    }

    private static JsonDocument? ParseJsonObject(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonDocument.Parse(json);
        }
        catch
        {
            return null;
        }
    }

    private static string BuildListSummary(JsonDocument? arguments)
    {
        var relativePath = ReadArgument(arguments, "relativePath", string.Empty);
        return string.IsNullOrWhiteSpace(relativePath)
            ? "List workspace"
            : $"List {relativePath}";
    }

    private static string BuildToolDetailTitle(ToolExecutionRecord toolRun)
        => toolRun.ToolName switch
        {
            "run_shell_command" => "Shell",
            "read_workspace_file" => "Read File",
            "search_workspace_text" => "Search Results",
            "list_workspace_files" => "Workspace Entries",
            "write_workspace_file" => "Write File",
            _ => HumanizeToolName(toolRun.ToolName)
        };

    private static string BuildToolDetailText(ToolExecutionRecord toolRun)
    {
        if (!string.IsNullOrWhiteSpace(toolRun.ResultContent))
        {
            return toolRun.ResultContent;
        }

        using var arguments = ParseJsonObject(toolRun.ArgumentsJson);

        return toolRun.ToolName switch
        {
            "run_shell_command" => BuildShellRequestDetails(arguments),
            "read_workspace_file" => ReadArgument(arguments, "relativePath", "No file path provided."),
            "search_workspace_text" => $"Query: {ReadArgument(arguments, "query", string.Empty)}",
            "list_workspace_files" => BuildListRequestDetails(arguments),
            "write_workspace_file" => BuildWriteRequestDetails(arguments),
            _ => toolRun.ResultSummary ?? PrettyPrintJson(toolRun.ArgumentsJson)
        };
    }

    private static string ResolveWriteVerb(ToolExecutionRecord toolRun)
    {
        if (!string.IsNullOrWhiteSpace(toolRun.ResultSummary) &&
            toolRun.ResultSummary.StartsWith("Created ", StringComparison.OrdinalIgnoreCase))
        {
            return "Create";
        }

        return "Write";
    }

    private static string Quote(string value)
        => string.IsNullOrWhiteSpace(value) ? "\"\"" : $"\"{value}\"";

    private static string ReadArgument(JsonDocument? arguments, string propertyName, string fallback, int? maxLength = null)
    {
        if (arguments?.RootElement.ValueKind == JsonValueKind.Object &&
            arguments.RootElement.TryGetProperty(propertyName, out var property))
        {
            var value = property.ValueKind switch
            {
                JsonValueKind.String => property.GetString(),
                JsonValueKind.Number => property.GetRawText(),
                JsonValueKind.True => bool.TrueString,
                JsonValueKind.False => bool.FalseString,
                _ => null
            };

            if (!string.IsNullOrWhiteSpace(value))
            {
                return TruncateInlineToolText(value, maxLength);
            }
        }

        return fallback;
    }

    private static string BuildShellRequestDetails(JsonDocument? arguments)
    {
        var command = ReadArgument(arguments, "command", string.Empty);
        return string.IsNullOrWhiteSpace(command)
            ? "No command payload was recorded."
            : $"$ {command.ReplaceLineEndings(Environment.NewLine)}";
    }

    private static string BuildListRequestDetails(JsonDocument? arguments)
    {
        var relativePath = ReadArgument(arguments, "relativePath", string.Empty);
        return string.IsNullOrWhiteSpace(relativePath)
            ? "Path: workspace root"
            : $"Path: {relativePath}";
    }

    private static string BuildWriteRequestDetails(JsonDocument? arguments)
    {
        var relativePath = ReadArgument(arguments, "relativePath", "Unknown path");
        var characterCount = ReadArgument(arguments, "characterCount", string.Empty);
        return string.IsNullOrWhiteSpace(characterCount)
            ? $"Path: {relativePath}"
            : $"Path: {relativePath}{Environment.NewLine}Characters: {characterCount}";
    }

    private static string TruncateInlineToolText(string value, int? maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value.ReplaceLineEndings(" ").Trim();
        if (!maxLength.HasValue || normalized.Length <= maxLength.Value)
        {
            return normalized;
        }

        return $"{normalized[..Math.Max(0, maxLength.Value - 1)]}…";
    }

    private static string HumanizeToolName(string toolName)
    {
        if (string.IsNullOrWhiteSpace(toolName))
        {
            return "Tool call";
        }

        var spaced = toolName.Replace('_', ' ').Trim().ToLowerInvariant();
        return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(spaced);
    }
}
