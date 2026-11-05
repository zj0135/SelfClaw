using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Infrastructure.Extensions.Mcp.Models;
using SelfClaw.Infrastructure.Network;

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
    private readonly INetworkProxySource _proxySource;

    public McpTransportFactory(ILoggerFactory? loggerFactory = null, INetworkProxySource? proxySource = null)
    {
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        _proxySource = proxySource ?? DefaultNetworkProxySource.Instance;
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
                ApplyProxyEnvironment(CreateStdioOptions(configuration, diagnostics)),
                _loggerFactory),
            McpTransportKind.Http => CreateHttpTransport(configuration),
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

    /// <summary>
    /// The SDK builds its own client when it is not handed one, and that client would ignore the network
    /// exit. The transport owns the client (and therefore the handler) so tearing a connection down
    /// releases the pool with it.
    /// </summary>
    private IClientTransport CreateHttpTransport(ResolvedMcpServerConfiguration configuration)
        => new HttpClientTransport(
            CreateHttpOptions(configuration),
            new HttpClient(NetworkProxyHttpHandler.Create(_proxySource.Current), disposeHandler: true),
            _loggerFactory,
            ownsHttpClient: true);

    /// <summary>
    /// A stdio server fetches its own dependencies (an npx-based server downloads its package on first
    /// run), so it needs the network exit too. The SDK applies these entries on top of the inherited
    /// environment, which is why only the proxy variables are written here.
    /// </summary>
    internal StdioClientTransportOptions ApplyProxyEnvironment(StdioClientTransportOptions options)
    {
        var entries = NetworkProxyEnvironment.Entries(_proxySource.Current);
        if (entries.Count == 0)
        {
            return options;
        }

        var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in options.EnvironmentVariables ?? new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase))
        {
            environment[entry.Key] = entry.Value;
        }

        foreach (var entry in entries)
        {
            environment[entry.Key] = entry.Value;
        }

        if (_proxySource.Current.Mode == NetworkProxyMode.Custom && !entries.ContainsKey("NO_PROXY"))
        {
            environment.Remove("NO_PROXY");
            environment.Remove("no_proxy");
        }

        options.EnvironmentVariables = environment;
        return options;
    }
}
