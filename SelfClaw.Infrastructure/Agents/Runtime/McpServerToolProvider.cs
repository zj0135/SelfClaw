using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;

namespace SelfClaw.Infrastructure.Agents.Runtime;

internal sealed class McpServerToolProvider : IAgentMcpToolProvider
{
    private const int MaxResultContentLength = 16_000;

    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<McpServerToolProvider> _logger;

    public McpServerToolProvider(ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<McpServerToolProvider>();
    }

    public async Task<ResolvedMcpTools> CreateToolsAsync(
        ChatTurnRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Agent.ConfiguredMcpServers.Count == 0)
        {
            return ResolvedMcpTools.Empty;
        }

        var tools = new List<AITool>();
        var metadataByToolName = new Dictionary<string, ToolInvocationMetadata>(StringComparer.OrdinalIgnoreCase);
        var resources = new List<IAsyncDisposable>();
        var usedToolNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var server in request.Agent.ConfiguredMcpServers)
        {
            StdioClientTransport? transport = null;
            McpClient? client = null;

            try
            {
                transport = new StdioClientTransport(
                    BuildTransportOptions(server, request.WorkspaceRoot),
                    _loggerFactory);

                client = await McpClient.CreateAsync(
                    transport,
                    new McpClientOptions(),
                    _loggerFactory,
                    cancellationToken);

                var serverTools = await client.ListToolsAsync(cancellationToken: cancellationToken);

                if (serverTools.Count == 0)
                {
                    await client.DisposeAsync();
                    continue;
                }

                foreach (var serverTool in serverTools)
                {
                    var toolName = BuildUniqueToolName(server.Id, serverTool.Name, usedToolNames);
                    var decoratedTool = serverTool
                        .WithName(toolName)
                        .WithDescription(BuildToolDescription(server, serverTool));

                    tools.Add(decoratedTool);
                    metadataByToolName[decoratedTool.Name] = ToolInvocationMetadata.CreateMcp(
                        server,
                        serverTool.Name,
                        request.ToolPermissionMode != ToolPermissionMode.FullAccess);
                }

                resources.Add(client);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(
                    exception,
                    "Failed to initialize MCP server '{ServerId}' for agent '{AgentId}'.",
                    server.Id,
                    request.Agent.Id);

                if (client is not null)
                {
                    await client.DisposeAsync();
                }
            }
        }

        return tools.Count == 0
            ? ResolvedMcpTools.Empty
            : new ResolvedMcpTools(tools, metadataByToolName, resources);
    }

    private static StdioClientTransportOptions BuildTransportOptions(
        AgentMcpServerDefinition server,
        WorkspaceRoot? workspaceRoot)
    {
        var environmentVariables = server.Env.Count == 0
            ? null
            : server.Env.ToDictionary(entry => entry.Key, entry => (string?)entry.Value, StringComparer.OrdinalIgnoreCase);
        var workingDirectory = string.IsNullOrWhiteSpace(workspaceRoot?.RootPath)
            ? null
            : workspaceRoot.RootPath;

        return new StdioClientTransportOptions
        {
            Name = server.EffectiveDisplayName,
            Command = server.Command,
            Arguments = [.. server.Args],
            WorkingDirectory = workingDirectory,
            EnvironmentVariables = environmentVariables
        };
    }

    private static string BuildUniqueToolName(
        string serverId,
        string toolName,
        ISet<string> usedToolNames)
    {
        var serverSegment = SanitizeToolSegment(serverId);
        var toolSegment = SanitizeToolSegment(toolName);
        var baseName = $"mcp_{serverSegment}_{toolSegment}";
        var candidate = baseName;
        var suffix = 2;

        while (!usedToolNames.Add(candidate))
        {
            candidate = $"{baseName}_{suffix}";
            suffix++;
        }

        return candidate;
    }

    private static string BuildToolDescription(AgentMcpServerDefinition server, McpClientTool tool)
    {
        var description = string.IsNullOrWhiteSpace(tool.Description)
            ? "Tool exposed by an MCP server."
            : tool.Description.Trim();

        return $"{description}\n\nProvided by MCP server '{server.EffectiveDisplayName}' (id: {server.Id}). Original MCP tool name: '{tool.Name}'.";
    }

    private static string SanitizeToolSegment(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "tool";
        }

        Span<char> buffer = stackalloc char[value.Length];
        var length = 0;

        foreach (var character in value)
        {
            buffer[length++] = char.IsLetterOrDigit(character)
                ? char.ToLowerInvariant(character)
                : '_';
        }

        var normalized = new string(buffer[..length]).Trim('_');
        while (normalized.Contains("__", StringComparison.Ordinal))
        {
            normalized = normalized.Replace("__", "_", StringComparison.Ordinal);
        }

        return string.IsNullOrWhiteSpace(normalized)
            ? "tool"
            : normalized;
    }
}
