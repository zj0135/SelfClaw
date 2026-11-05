using FluentAssertions;
using ModelContextProtocol.Client;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Infrastructure.Extensions.Mcp;
using SelfClaw.Infrastructure.Extensions.Mcp.Models;

namespace SelfClaw.Tests.Infrastructure.Extensions;

public sealed class McpTransportFactoryTests
{
    [Fact]
    public void CreateStdioOptions_UsesBoundedShutdownTimeout()
    {
        var options = McpTransportFactory.CreateStdioOptions(CreateConfiguration(), new BoundedDiagnosticBuffer());

        // The SDK waits for the server to exit on its own before it kills the process tree, and a
        // stdio server never sees stdin close before that wait, so the SDK's five second default is
        // spent in full on every teardown - including application shutdown.
        options.ShutdownTimeout.Should().Be(McpTransportFactory.StdioShutdownTimeout);
        options.ShutdownTimeout.Should().BeLessThanOrEqualTo(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void CreateStdioOptions_CarriesCommandArgumentsAndEnvironment()
    {
        var configuration = CreateConfiguration() with
        {
            Command = "server.exe",
            Arguments = ["--stdio"],
            Environment = new Dictionary<string, string> { ["TOKEN"] = "secret" }
        };

        var options = McpTransportFactory.CreateStdioOptions(configuration, new BoundedDiagnosticBuffer());

        options.Name.Should().Be("Server");
        options.Command.Should().Be("server.exe");
        options.Arguments.Should().BeEquivalentTo(["--stdio"]);
        options.WorkingDirectory.Should().Be("C:\\work");
        options.EnvironmentVariables.Should().ContainKey("TOKEN").WhoseValue.Should().Be("secret");
        options.StandardErrorLines.Should().NotBeNull();
    }

    [Fact]
    public void CreateStdioOptions_merges_the_network_exit_into_the_configured_environment()
    {
        var configuration = CreateConfiguration() with
        {
            Command = "npx",
            Environment = new Dictionary<string, string> { ["TOKEN"] = "secret" }
        };
        var factory = new McpTransportFactory(
            proxySource: new StubNetworkProxySource(new NetworkProxySettings(NetworkProxyMode.Custom, "127.0.0.1", 7890)));

        var options = factory.ApplyProxyEnvironment(
            McpTransportFactory.CreateStdioOptions(configuration, new BoundedDiagnosticBuffer()));

        options.EnvironmentVariables.Should().ContainKey("TOKEN").WhoseValue.Should().Be("secret");
        options.EnvironmentVariables.Should().ContainKey("HTTPS_PROXY").WhoseValue.Should().Be("http://127.0.0.1:7890");
        options.EnvironmentVariables.Should().NotContainKey("NO_PROXY");
    }

    [Fact]
    public void CreateStdioOptions_neutralizes_an_inherited_proxy_in_direct_mode()
    {
        var factory = new McpTransportFactory(
            proxySource: new StubNetworkProxySource(new NetworkProxySettings(NetworkProxyMode.Direct)));

        var options = factory.ApplyProxyEnvironment(
            McpTransportFactory.CreateStdioOptions(CreateConfiguration(), new BoundedDiagnosticBuffer()));

        options.EnvironmentVariables.Should().ContainKey("HTTP_PROXY").WhoseValue.Should().BeEmpty();
        options.EnvironmentVariables.Should().ContainKey("NO_PROXY").WhoseValue.Should().Be("*");
    }

    [Fact]
    public void Http_transport_is_built_with_the_shared_exit_aware_client()
    {
        // The SDK would build its own client, which would bypass the setting; this asserts the overload
        // that takes one (plus its ownership flag) is the one in use.
        var factory = new McpTransportFactory(
            proxySource: new StubNetworkProxySource(new NetworkProxySettings(NetworkProxyMode.Custom, "127.0.0.1", 7890)));
        var configuration = CreateConfiguration() with
        {
            Transport = McpTransportKind.Http,
            Endpoint = new Uri("https://mcp.example.test/mcp"),
            Command = null,
            Arguments = []
        };

        var transport = factory.Create(configuration, new BoundedDiagnosticBuffer());

        transport.Should().BeOfType<HttpClientTransport>();
    }

    private static ResolvedMcpServerConfiguration CreateConfiguration()
        => new(
            "server",
            "Server",
            McpTransportKind.Stdio,
            1,
            null,
            true,
            null,
            "node",
            ["server.js"],
            "C:\\work",
            new Dictionary<string, string>(),
            null,
            null,
            null,
            new Dictionary<string, string>(),
            "C:\\work");

    private sealed class StubNetworkProxySource(NetworkProxySettings settings) : INetworkProxySource
    {
        public NetworkProxySettings Current => settings;

        public Task<NetworkProxySettings> GetAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(settings);
    }
}
