using System.Diagnostics;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using SelfClaw.Core.Models;
using SelfClaw.Infrastructure.Extensions.Mcp;
using SelfClaw.Infrastructure.Extensions.Mcp.Models;

namespace SelfClaw.Tests.Infrastructure.Extensions;

public sealed class McpStdioProcessTreeSpikeTests
{
    [Fact]
    public async Task DisposeAsync_TerminatesStdioProcessTree()
    {
        var nodePath = FindNodePath();
        nodePath.Should().NotBeNull("the repository's Vue toolchain requires Node.js");
        var fixturePath = Path.Combine(
            AppContext.BaseDirectory,
            "Infrastructure",
            "Extensions",
            "Fixtures",
            "mcp-process-tree-fixture.js");
        File.Exists(fixturePath).Should().BeTrue();
        var pidFile = Path.Combine(Path.GetTempPath(), $"selfclaw-mcp-spike-{Guid.NewGuid():N}.json");
        ProcessIds? processIds = null;

        try
        {
            var transport = new StdioClientTransport(
                new StdioClientTransportOptions
                {
                    Name = "SelfClaw process tree spike",
                    Command = nodePath!,
                    Arguments = [fixturePath, pidFile],
                    ShutdownTimeout = TimeSpan.FromSeconds(1)
                },
                NullLoggerFactory.Instance);
            await using (var client = await McpClient.CreateAsync(
                transport,
                loggerFactory: NullLoggerFactory.Instance))
            {
                await client.PingAsync();
                var tools = await client.ListToolsAsync();
                tools.Should().ContainSingle(tool => tool.Name == "fixture_echo");
                var result = await tools.Single().InvokeAsync(new AIFunctionArguments { ["value"] = "stdio" });
                result.Should().BeOfType<TextContent>().Which.Text.Should().Be("echo: stdio");
                processIds = await ReadProcessIdsAsync(pidFile);
                IsRunning(processIds.ParentPid).Should().BeTrue();
                IsRunning(processIds.ChildPid).Should().BeTrue();
            }

            await WaitUntilStoppedAsync(processIds.ParentPid, TimeSpan.FromSeconds(5));
            await WaitUntilStoppedAsync(processIds.ChildPid, TimeSpan.FromSeconds(5));
            IsRunning(processIds.ParentPid).Should().BeFalse();
            IsRunning(processIds.ChildPid).Should().BeFalse();
        }
        finally
        {
            if (processIds is not null)
            {
                KillIfRunning(processIds.ParentPid);
                KillIfRunning(processIds.ChildPid);
            }

            if (File.Exists(pidFile))
            {
                File.Delete(pidFile);
            }
        }
    }

    [Fact]
    public async Task ConnectAsync_CancellationDuringInitialize_TerminatesStdioProcessTree()
    {
        var nodePath = FindNodePath();
        nodePath.Should().NotBeNull("the repository's Vue toolchain requires Node.js");
        var fixturePath = Path.Combine(
            AppContext.BaseDirectory,
            "Infrastructure",
            "Extensions",
            "Fixtures",
            "mcp-process-tree-fixture.js");
        var pidFile = Path.Combine(Path.GetTempPath(), $"selfclaw-mcp-cancel-{Guid.NewGuid():N}.json");
        ProcessIds? processIds = null;

        try
        {
            var configuration = new ResolvedMcpServerConfiguration(
                "stdio-cancellation-fixture",
                "stdio cancellation fixture",
                McpTransportKind.Stdio,
                1,
                null,
                true,
                null,
                nodePath,
                [fixturePath, pidFile, "--hang-initialize"],
                null,
                new Dictionary<string, string>(),
                null,
                null,
                null,
                new Dictionary<string, string>(),
                null);
            var factory = new SdkMcpClientConnectionFactory(new McpTransportFactory());
            using var cancellation = new CancellationTokenSource();

            var connectTask = factory.ConnectAsync(configuration, cancellation.Token);
            processIds = await ReadProcessIdsAsync(pidFile);
            cancellation.Cancel();

            var waitForConnection = async () => await connectTask;
            await waitForConnection.Should().ThrowAsync<OperationCanceledException>();
            await WaitUntilStoppedAsync(processIds.ParentPid, TimeSpan.FromSeconds(5));
            await WaitUntilStoppedAsync(processIds.ChildPid, TimeSpan.FromSeconds(5));
            IsRunning(processIds.ParentPid).Should().BeFalse();
            IsRunning(processIds.ChildPid).Should().BeFalse();
        }
        finally
        {
            if (processIds is not null)
            {
                KillIfRunning(processIds.ParentPid);
                KillIfRunning(processIds.ChildPid);
            }

            if (File.Exists(pidFile))
            {
                File.Delete(pidFile);
            }
        }
    }

    private static string? FindNodePath()
    {
        var pathDirectories = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return pathDirectories
            .Select(directory => Path.Combine(directory, "node.exe"))
            .FirstOrDefault(File.Exists);
    }

    private static async Task<ProcessIds> ReadProcessIdsAsync(string path)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            if (File.Exists(path))
            {
                var json = await File.ReadAllTextAsync(path);
                return JsonSerializer.Deserialize<ProcessIds>(json, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                }) ?? throw new InvalidOperationException("The MCP fixture PID file was empty.");
            }

            await Task.Delay(100);
        }

        throw new TimeoutException("The MCP fixture did not write its process IDs.");
    }

    private static async Task WaitUntilStoppedAsync(int processId, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline && IsRunning(processId))
        {
            await Task.Delay(100);
        }
    }

    private static bool IsRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static void KillIfRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(2000);
            }
        }
        catch (ArgumentException)
        {
        }
    }

    private sealed record ProcessIds(int ParentPid, int ChildPid);
}
