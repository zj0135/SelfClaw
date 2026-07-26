using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Core.Runtime.Agent;
using SelfClaw.Infrastructure.Extensions.Mcp.Models;
using SelfClaw.Infrastructure.Extensions.Runtime;
using SelfClaw.Infrastructure.Extensions.Runtime.Models;

namespace SelfClaw.Infrastructure.Extensions.Mcp;

internal sealed class McpToolAdapter
{
    internal const int MaximumProviderNameLength = 64;
    internal const int MaximumModelResultCharacters = 64 * 1024;

    public (AIFunction Tool, DirectToolDescriptor Descriptor) Create(
        McpClientTool tool,
        ResolvedMcpServerConfiguration configuration,
        Guid conversationId,
        ToolPermissionMode permissionMode,
        IToolApprovalHandler? approvalHandler)
    {
        ArgumentNullException.ThrowIfNull(tool);
        ArgumentNullException.ThrowIfNull(configuration);
        var originalName = tool.ProtocolTool.Name;
        var providerName = CreateProviderName(configuration.Id, originalName);
        var displayName = tool.ProtocolTool.Annotations?.Title
            ?? tool.Title
            ?? originalName;
        var annotationsJson = tool.ProtocolTool.Annotations is null
            ? null
            : JsonSerializer.Serialize(tool.ProtocolTool.Annotations);
        var description = tool.Description.Length <= 1024
            ? tool.Description
            : tool.Description[..1024];
        var renamed = tool.WithName(providerName).WithDescription(description);
        var transportSummary = configuration.Transport == McpTransportKind.Stdio
            ? $"stdio: {configuration.Command}"
            : $"http: {configuration.Endpoint?.Host}";
        var approved = new ApprovedAIFunction(
            renamed,
            conversationId,
            permissionMode,
            approvalHandler,
            displayName,
            ToolSourceKind.Mcp,
            configuration.Id,
            transportSummary,
            annotationsJson,
            LimitModelResult);
        var kind = tool.ProtocolTool.Annotations?.ReadOnlyHint == true
            ? ToolCallKind.Read
            : ToolCallKind.Other;
        var descriptor = new DirectToolDescriptor(
            providerName,
            kind,
            ToolSourceKind.Mcp,
            configuration.Id,
            displayName,
            originalName,
            transportSummary,
            annotationsJson);
        return (approved, descriptor);
    }

    internal static string CreateProviderName(string serverId, string toolName)
    {
        var fullName = $"mcp__{Slug(serverId)}__{Slug(toolName)}";
        if (fullName.Length <= MaximumProviderNameLength)
        {
            return fullName;
        }

        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(fullName)))[..8];
        return $"{fullName[..(MaximumProviderNameLength - hash.Length - 1)]}_{hash}";
    }

    internal static object? LimitModelResult(object? result)
    {
        if (result is TextContent textContent)
        {
            return textContent.Text.Length <= MaximumModelResultCharacters
                ? result
                : new TextContent(
                    textContent.Text[..(MaximumModelResultCharacters - 128)] +
                    "\n[SelfClaw truncated the MCP tool result at 64 KiB.]");
        }

        if (result is not JsonElement element)
        {
            return result;
        }

        var json = element.GetRawText();
        if (json.Length <= MaximumModelResultCharacters)
        {
            return result;
        }

        var isError = element.ValueKind == JsonValueKind.Object &&
                      element.TryGetProperty("isError", out var errorElement) &&
                      errorElement.ValueKind == JsonValueKind.True;
        return JsonSerializer.SerializeToElement(new
        {
            content = new[]
            {
                new
                {
                    type = "text",
                    text = json[..(MaximumModelResultCharacters - 1024)] +
                           "\n[SelfClaw truncated the MCP tool result at 64 KiB.]"
                }
            },
            isError,
            truncated = true
        });
    }

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

    private static string Slug(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value.ToLowerInvariant())
        {
            builder.Append(char.IsAsciiLetterOrDigit(character) || character is '_' or '-'
                ? character
                : '_');
        }

        return builder.Length == 0 ? "unnamed" : builder.ToString();
    }

    private static string FirstLine(string value)
    {
        var line = value.Split(['\r', '\n'], 2, StringSplitOptions.None)[0].Trim();
        if (line.Length == 0)
        {
            return "MCP tool call completed.";
        }

        return line.Length <= 160 ? line : line[..160] + "...";
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
