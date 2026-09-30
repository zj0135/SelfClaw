using FluentAssertions;
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
}
