using System.Text.Json;
using Microsoft.Extensions.AI;
using SelfClaw.Core.Runtime.Agent;
using SelfClaw.Infrastructure.Agents.Direct.Tools.Models;

namespace SelfClaw.Infrastructure.Agents.Direct.Tools;

internal static class McpToolResultFormatter
{
    internal const int MaximumModelResultBytes = 64 * 1024;
    private const string TruncationNotice = "\n[SelfClaw truncated the MCP tool result at 64 KiB.]";

    internal static DirectToolResult Format(object? result)
    {
        if (result is IEnumerable<AIContent> contents) result = contents.ToArray();
        var (status, summary, detail) = DescribeResult(result);
        var content = result is JsonElement json ? json.Clone() : JsonSerializer.SerializeToElement(result);
        var formatted = new DirectToolResult(status, FirstLine(summary), content, detail);
        if (SerializedBytes(formatted) <= MaximumModelResultBytes) return formatted;

        var lower = 0;
        var upper = Math.Min(detail.Length, MaximumModelResultBytes);
        while (lower < upper)
        {
            var count = lower + (upper - lower + 1) / 2;
            if (SerializedBytes(TruncatedResult(formatted, detail, count)) <= MaximumModelResultBytes) lower = count;
            else upper = count - 1;
        }

        return TruncatedResult(formatted, detail, lower);
    }

    private static DirectToolResult TruncatedResult(DirectToolResult result, string detail, int length)
    {
        var text = Prefix(detail, length) + TruncationNotice;
        var content = JsonSerializer.SerializeToElement(new
        {
            content = new[] { new { type = "text", text } },
            isError = result.Status == ToolCallStatus.Failed,
            truncated = true
        });
        return result with { Content = content, Detail = text };
    }

    private static int SerializedBytes(DirectToolResult result)
        => JsonSerializer.SerializeToUtf8Bytes(result).Length;

    internal static (ToolCallStatus Status, string Summary, string Detail) DescribeResult(object? result)
    {
        if (result is TextContent textContent)
        {
            return (ToolCallStatus.Completed, FirstLine(textContent.Text), textContent.Text);
        }

        if (result is IEnumerable<AIContent> contents)
        {
            var values = contents.ToArray();
            var texts = values.OfType<TextContent>().Select(content => content.Text).ToArray();
            var contentPlaceholders = values
                .Where(content => content is not TextContent)
                .Select(content => $"[{content.GetType().Name}]")
                .ToArray();
            var contentDetail = string.Join(Environment.NewLine, texts.Concat(contentPlaceholders));
            return (
                ToolCallStatus.Completed,
                texts.FirstOrDefault() is { } first ? FirstLine(first) : $"{values.Length} MCP content items",
                contentDetail);
        }

        if (result is not JsonElement element || element.ValueKind != JsonValueKind.Object)
        {
            var text = result?.ToString() ?? "Tool call completed.";
            return (ToolCallStatus.Completed, FirstLine(text), text);
        }

        var isError = element.TryGetProperty("isError", out var errorElement) && errorElement.ValueKind == JsonValueKind.True;
        var textBlocks = new List<string>();
        var placeholders = new List<string>();
        var typeCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (element.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
        {
            foreach (var block in content.EnumerateArray())
            {
                var type = block.TryGetProperty("type", out var typeElement)
                    ? typeElement.GetString() ?? "content"
                    : "content";
                typeCounts[type] = typeCounts.GetValueOrDefault(type) + 1;
                if (string.Equals(type, "text", StringComparison.OrdinalIgnoreCase) &&
                    block.TryGetProperty("text", out var textElement) &&
                    textElement.ValueKind == JsonValueKind.String)
                {
                    textBlocks.Add(textElement.GetString() ?? string.Empty);
                    continue;
                }

                var mimeType = block.TryGetProperty("mimeType", out var mimeElement)
                    ? mimeElement.GetString()
                    : null;
                var byteLength = block.TryGetProperty("data", out var dataElement) &&
                                 dataElement.ValueKind == JsonValueKind.String
                    ? EstimateBase64Bytes(dataElement.GetString())
                    : null;
                placeholders.Add($"[{type}{FormatMetadata(mimeType, byteLength)}]");
            }
        }

        var details = new List<string>();
        details.AddRange(textBlocks.Where(text => !string.IsNullOrWhiteSpace(text)));
        details.AddRange(placeholders);
        if (element.TryGetProperty("structuredContent", out var structured) &&
            structured.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
        {
            details.Add(JsonSerializer.Serialize(structured, new JsonSerializerOptions { WriteIndented = true }));
        }

        var detail = details.Count == 0 ? element.GetRawText() : string.Join(Environment.NewLine, details);
        var summary = textBlocks.FirstOrDefault(text => !string.IsNullOrWhiteSpace(text)) is { } firstText
            ? FirstLine(firstText)
            : typeCounts.Count == 0
                ? "MCP tool call completed."
                : string.Join(", ", typeCounts.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                    .Select(pair => $"{pair.Value} {pair.Key}"));
        return (isError ? ToolCallStatus.Failed : ToolCallStatus.Completed, summary, detail);
    }

    private static string FirstLine(string value)
    {
        var newline = value.IndexOfAny(['\r', '\n']);
        var line = (newline < 0 ? value.AsSpan() : value.AsSpan(0, newline)).Trim();
        if (line.Length == 0)
        {
            return "MCP tool call completed.";
        }

        return line.Length <= 160 ? line.ToString() : Prefix(line, 160) + "...";
    }

    private static string Prefix(ReadOnlySpan<char> text, int length)
    {
        if (length > 0 && length < text.Length && char.IsHighSurrogate(text[length - 1]) && char.IsLowSurrogate(text[length])) length--;
        return text[..length].ToString();
    }

    private static long? EstimateBase64Bytes(string? value)
        => string.IsNullOrEmpty(value) ? null : value.Length * 3L / 4L;

    private static string FormatMetadata(string? mimeType, long? byteLength)
    {
        var values = new List<string>();
        if (!string.IsNullOrWhiteSpace(mimeType))
        {
            values.Add(mimeType);
        }

        if (byteLength is long length)
        {
            values.Add($"{length} bytes");
        }

        return values.Count == 0 ? string.Empty : ": " + string.Join(", ", values);
    }
}
