using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using SelfClaw.Core.Models;
using SelfClaw.Infrastructure.Extensions.Mcp.Models;

namespace SelfClaw.Infrastructure.Extensions.Mcp;

internal sealed class McpTransportFactory
{
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
        var environment = CreateWindowsBaselineEnvironment();
        foreach (var entry in configuration.Environment)
        {
            environment[entry.Key] = entry.Value;
        }

        return new StdioClientTransportOptions
        {
            Name = configuration.DisplayName,
            Command = configuration.Command!,
            Arguments = configuration.Arguments.ToArray(),
            WorkingDirectory = configuration.WorkingDirectory,
            InheritEnvironmentVariables = false,
            EnvironmentVariables = environment,
            StandardErrorLines = diagnostics.Append
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

    internal static Dictionary<string, string?> CreateWindowsBaselineEnvironment()
    {
        var systemRoot = Environment.GetEnvironmentVariable("SystemRoot")
            ?? Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var system32 = Path.Combine(systemRoot, "System32");
        var path = string.Join(Path.PathSeparator,
            system32,
            systemRoot,
            Path.Combine(system32, "Wbem"),
            Path.Combine(system32, "WindowsPowerShell", "v1.0"));

        return new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["SystemRoot"] = systemRoot,
            ["windir"] = systemRoot,
            ["ComSpec"] = Environment.GetEnvironmentVariable("ComSpec") ?? Path.Combine(system32, "cmd.exe"),
            ["PATHEXT"] = Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD",
            ["TEMP"] = Path.GetTempPath(),
            ["TMP"] = Path.GetTempPath(),
            ["PATH"] = path
        };
    }
}
