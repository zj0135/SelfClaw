using SelfClaw.Infrastructure.Agents.Direct.Tools.Models;
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

namespace SelfClaw.Infrastructure.Agents.Direct.Tools;

internal sealed class McpToolAdapter
{
    internal const int MaximumProviderNameLength = 64;

    public DirectToolBinding Create(
        McpClientTool tool,
        ResolvedMcpServerConfiguration configuration)
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
        var kind = tool.ProtocolTool.Annotations?.ReadOnlyHint == true
            ? ToolCallKind.Read
            : ToolCallKind.Other;
        var descriptor = new DirectToolDescriptor(
            providerName,
            kind,
            ToolSourceKind.Mcp,
            configuration.Id,
            displayName);
        return new DirectToolBinding(new McpResultFunction(renamed), descriptor, true, transportSummary, annotationsJson);
    }

    private sealed class McpResultFunction(AIFunction inner) : DelegatingAIFunction(inner)
    {
        protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
        {
            var result = await InnerFunction.InvokeAsync(arguments, cancellationToken).ConfigureAwait(false);
            return McpToolResultFormatter.Format(result);
        }
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

}
