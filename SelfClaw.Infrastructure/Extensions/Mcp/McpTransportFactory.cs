using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using SelfClaw.Core.Models;
using SelfClaw.Infrastructure.Extensions.Mcp.Models;

namespace SelfClaw.Infrastructure.Extensions.Mcp;

internal sealed class McpTransportFactory
{
    /// <summary>
    /// How long the SDK waits for a stdio server to exit on its own before it kills the process tree.
    /// The SDK's five second default is spent in full: the child does not see stdin close before that
    /// wait, so an npx/node server never exits voluntarily. Teardown happens on the application
    /// shutdown path and on every configuration revision change, so a shorter grace period keeps the
    /// close responsive; the tree is still terminated afterwards.
    /// </summary>
    internal static readonly TimeSpan StdioShutdownTimeout = TimeSpan.FromSeconds(1);

    private readonly ILoggerFactory _loggerFactory;

    public McpTransportFactory(ILoggerFactory? loggerFactory = null)
    {
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
    }

    public IClientTransport Create(
        ResolvedMcpServerConfiguration configuration,
        BoundedDiagnosticBuffer diagnostics)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(diagnostics);
        if (!configuration.IsAvailable)
        {
            throw new InvalidOperationException(configuration.UnavailableReason ?? "MCP server is unavailable.");
        }

        return configuration.Transport switch
        {
            McpTransportKind.Stdio => new StdioClientTransport(
                CreateStdioOptions(configuration, diagnostics),
                _loggerFactory),
            McpTransportKind.Http => new HttpClientTransport(
                CreateHttpOptions(configuration),
                _loggerFactory),
            _ => throw new ArgumentOutOfRangeException(
                nameof(configuration),
                configuration.Transport,
                "Unsupported MCP transport.")
        };
    }

    internal static StdioClientTransportOptions CreateStdioOptions(
        ResolvedMcpServerConfiguration configuration,
        BoundedDiagnosticBuffer diagnostics)
    {
        var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in configuration.Environment)
        {
            environment[entry.Key] = entry.Value;
        }

        return new StdioClientTransportOptions
        {
            Name = configuration.DisplayName,
            Command = configuration.Command!,
            Arguments = [.. configuration.Arguments],
            WorkingDirectory = configuration.WorkingDirectory,
            EnvironmentVariables = environment.Count == 0 ? null : environment,
            StandardErrorLines = diagnostics.Append,
            ShutdownTimeout = StdioShutdownTimeout
        };
    }

    internal static HttpClientTransportOptions CreateHttpOptions(
        ResolvedMcpServerConfiguration configuration)
        => new()
        {
            Name = configuration.DisplayName,
            Endpoint = configuration.Endpoint!,
            TransportMode = configuration.TransportMode switch
            {
                "streamableHttp" => HttpTransportMode.StreamableHttp,
                "sse" => HttpTransportMode.Sse,
                _ => HttpTransportMode.AutoDetect
            },
            AdditionalHeaders = configuration.Headers.ToDictionary(pair => pair.Key, pair => pair.Value),
            ConnectionTimeout = configuration.ConnectionTimeout ?? TimeSpan.FromSeconds(30)
        };

}
