using System.Globalization;
using System.Text.Json;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime.Agent;
using SelfClaw.Desktop.Services.Transcript;

namespace SelfClaw.Desktop.Services;

internal static class TranscriptToolRunPresenter
{
    private static readonly JsonSerializerOptions IndentedJsonOptions = new() { WriteIndented = true };

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
            BuildToolDetailText(toolRun),
            toolRun.ToolName,
            toolRun.SourceKind?.ToString().ToLowerInvariant(),
            toolRun.SourceId,
            toolRun.DisplayName);

    private static string BuildInlineToolSummary(ToolExecutionRecord toolRun)
    {
        if (toolRun.SourceKind is ToolSourceKind.Mcp or ToolSourceKind.Skill or ToolSourceKind.Plugin)
        {
            return HumanizeToolName(toolRun.DisplayName ?? toolRun.ToolName);
        }

        using var arguments = ParseJsonObject(toolRun.ArgumentsJson);

        return toolRun.ToolName switch
        {
            "read_file" => $"Read {ReadArgument(arguments, "relativePath", "file")}",
            "write_file" => $"{ResolveWriteVerb(toolRun)} {ReadArgument(arguments, "relativePath", "file")}",
            "edit_file" => $"Edit {ReadArgument(arguments, "relativePath", "file")}",
            "run_shell_command" => $"Run {ReadArgument(arguments, "command", "command", maxLength: 44)}",
            "list_files" or "glob_files" => BuildListSummary(arguments),
            "search_text" => $"Search {Quote(ReadArgument(arguments, "query", "text", maxLength: 28))}",
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
        => toolRun.SourceKind is ToolSourceKind.Mcp or ToolSourceKind.Skill or ToolSourceKind.Plugin
            ? toolRun.DisplayName ?? HumanizeToolName(toolRun.ToolName)
            : toolRun.ToolName switch
            {
                "run_shell_command" => "Shell",
                "read_file" => "Read File",
                "search_text" => "Search Results",
                "list_files" or "glob_files" => "Workspace Entries",
                "write_file" => "Write File",
                "edit_file" => "Edit File",
                _ => HumanizeToolName(toolRun.ToolName)
            };

    private static string BuildToolDetailText(ToolExecutionRecord toolRun)
    {
        if (!string.IsNullOrWhiteSpace(toolRun.ResultContent))
        {
            return TranscriptToolResultLimiter.LimitDisplayed(toolRun.ResultContent);
        }

        using var arguments = ParseJsonObject(toolRun.ArgumentsJson);

        var detail = toolRun.ToolName switch
        {
            "run_shell_command" => BuildShellRequestDetails(arguments),
            "read_file" => ReadArgument(arguments, "relativePath", "No file path provided."),
            "search_text" => $"Query: {ReadArgument(arguments, "query", string.Empty)}",
            "list_files" or "glob_files" => BuildListRequestDetails(arguments),
            "write_file" or "edit_file" => BuildWriteRequestDetails(arguments),
            _ => toolRun.ResultSummary ?? PrettyPrintJson(toolRun.ArgumentsJson)
        };
        return TranscriptToolResultLimiter.LimitDisplayed(detail);
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
