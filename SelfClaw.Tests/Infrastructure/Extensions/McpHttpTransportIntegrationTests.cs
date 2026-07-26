using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Core.Runtime.Agent;
using SelfClaw.Infrastructure.Extensions.Mcp;
using SelfClaw.Infrastructure.Extensions.Mcp.Models;

namespace SelfClaw.Tests.Infrastructure.Extensions;

public sealed class McpHttpTransportIntegrationTests
{
    [Fact]
    public async Task Http_transport_initializes_discovers_invokes_and_pings_fixture()
    {
        await using var fixture = await HttpMcpFixture.StartAsync();
        var configuration = new ResolvedMcpServerConfiguration(
            "http-fixture",
            "HTTP fixture",
            McpTransportKind.Http,
            1,
            null,
            true,
            null,
            null,
            [],
            null,
            new Dictionary<string, string>(),
            fixture.Endpoint,
            "streamableHttp",
            TimeSpan.FromSeconds(5),
            new Dictionary<string, string>(),
            null);
        var factory = new SdkMcpClientConnectionFactory(new McpTransportFactory());

        await using var connection = await factory.ConnectAsync(configuration);
        var tool = connection.Tools.Should().ContainSingle().Subject;
        tool.Name.Should().Be("fixture_echo");
        var adapted = new McpToolAdapter().Create(
            tool,
            configuration,
            Guid.NewGuid(),
            ToolPermissionMode.FullAccess,
            null);
        adapted.Tool.Name.Should().Be("mcp__http-fixture__fixture_echo");

        var result = await adapted.Tool.InvokeAsync(new AIFunctionArguments { ["value"] = "hello" });
        await connection.PingAsync();

        result.Should().BeOfType<TextContent>().Which.Text.Should().Be("echo: hello");
        McpToolAdapter.DescribeResult(result).Should().Be(
            (ToolCallStatus.Completed, "echo: hello", "echo: hello"));
        fixture.ReturnToolError = true;
        var errorResult = await adapted.Tool.InvokeAsync(new AIFunctionArguments { ["value"] = "blocked" });
        var errorElement = errorResult.Should().BeOfType<JsonElement>().Subject;
        errorElement.GetProperty("isError").GetBoolean().Should().BeTrue();
        McpToolAdapter.DescribeResult(errorElement).Status.Should().Be(ToolCallStatus.Failed);
        fixture.CalledToolNames.Should().OnlyContain(name => name == "fixture_echo");
        fixture.Methods.Should().ContainInOrder("initialize", "notifications/initialized", "tools/list", "tools/call", "ping");
    }

    private sealed class HttpMcpFixture : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _shutdown = new();
        private readonly Task _serverTask;

        private HttpMcpFixture(TcpListener listener, Uri endpoint)
        {
            _listener = listener;
            Endpoint = endpoint;
            _serverTask = RunAsync();
        }

        public Uri Endpoint { get; }
        public List<string> Methods { get; } = [];
        public List<string> CalledToolNames { get; } = [];
        public bool ReturnToolError { get; set; }

        public static Task<HttpMcpFixture> StartAsync()
        {
            var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
            listener.Start();
            var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
            var endpoint = new Uri($"http://127.0.0.1:{port}/mcp");
            return Task.FromResult(new HttpMcpFixture(listener, endpoint));
        }

        public async ValueTask DisposeAsync()
        {
            await _shutdown.CancelAsync();
            _listener.Stop();
            try
            {
                await _serverTask;
            }
            catch (SocketException) when (_shutdown.IsCancellationRequested)
            {
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
            {
            }

            _shutdown.Dispose();
        }

        private async Task RunAsync()
        {
            while (!_shutdown.IsCancellationRequested)
            {
                using var client = await _listener.AcceptTcpClientAsync(_shutdown.Token);
                await HandleAsync(client, _shutdown.Token);
            }
        }

        private async Task HandleAsync(TcpClient client, CancellationToken cancellationToken)
        {
            await using var stream = client.GetStream();
            var request = await ReadRequestAsync(stream, cancellationToken);
            if (!string.Equals(request.Method, "POST", StringComparison.Ordinal))
            {
                await WriteResponseAsync(stream, 405, ReadOnlyMemory<byte>.Empty, cancellationToken);
                return;
            }

            using var document = JsonDocument.Parse(request.Body);
            var root = document.RootElement;
            var method = root.GetProperty("method").GetString() ?? string.Empty;
            lock (Methods)
            {
                Methods.Add(method);
            }

            if (!root.TryGetProperty("id", out var id))
            {
                await WriteResponseAsync(stream, 202, ReadOnlyMemory<byte>.Empty, cancellationToken);
                return;
            }

            object result = method switch
            {
                "initialize" => new
                {
                    protocolVersion = root.GetProperty("params").GetProperty("protocolVersion").GetString(),
                    capabilities = new { tools = new { listChanged = false } },
                    serverInfo = new { name = "selfclaw-http-fixture", version = "1.0.0" }
                },
                "tools/list" => new
                {
                    tools = new[]
                    {
                        new
                        {
                            name = "fixture_echo",
                            description = "Echo fixture",
                            inputSchema = new
                            {
                                type = "object",
                                properties = new { value = new { type = "string" } },
                                required = new[] { "value" }
                            }
                        }
                    }
                },
                "tools/call" => CreateToolResult(root),
                "ping" => new { },
                _ => throw new InvalidOperationException($"Unexpected MCP method '{method}'.")
            };
            var response = JsonSerializer.SerializeToUtf8Bytes(new
            {
                jsonrpc = "2.0",
                id = id.Clone(),
                result
            });
            await WriteResponseAsync(stream, 200, response, cancellationToken);
        }

        private object CreateToolResult(JsonElement request)
        {
            CalledToolNames.Add(request.GetProperty("params").GetProperty("name").GetString() ?? string.Empty);
            var value = request.GetProperty("params").GetProperty("arguments").GetProperty("value").GetString();
            return new
            {
                content = new[] { new { type = "text", text = $"echo: {value}" } },
                isError = ReturnToolError
            };
        }

        private static async Task<HttpRequest> ReadRequestAsync(
            NetworkStream stream,
            CancellationToken cancellationToken)
        {
            var headerBytes = new List<byte>();
            var terminator = new byte[] { 13, 10, 13, 10 };
            var buffer = new byte[1];
            while (!headerBytes.TakeLast(4).SequenceEqual(terminator))
            {
                if (await stream.ReadAsync(buffer, cancellationToken) == 0)
                {
                    throw new EndOfStreamException("HTTP request ended before headers completed.");
                }

                headerBytes.Add(buffer[0]);
                if (headerBytes.Count > 64 * 1024)
                {
                    throw new InvalidDataException("HTTP request headers exceeded the fixture limit.");
                }
            }

            var headerText = Encoding.ASCII.GetString(headerBytes.ToArray());
            var lines = headerText.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
            var method = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
            var contentLength = lines
                .Select(line => line.Split(':', 2))
                .Where(parts => parts.Length == 2 && parts[0].Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                .Select(parts => int.Parse(parts[1].Trim()))
                .SingleOrDefault();
            var isChunked = lines.Any(line =>
                line.Equals("Transfer-Encoding: chunked", StringComparison.OrdinalIgnoreCase));
            var body = isChunked
                ? await ReadChunkedBodyAsync(stream, cancellationToken)
                : new byte[contentLength];
            if (!isChunked)
            {
                await stream.ReadExactlyAsync(body, cancellationToken);
            }

            return new HttpRequest(method, body);
        }

        private static async Task<byte[]> ReadChunkedBodyAsync(
            NetworkStream stream,
            CancellationToken cancellationToken)
        {
            using var body = new MemoryStream();
            while (true)
            {
                var sizeLine = await ReadAsciiLineAsync(stream, cancellationToken);
                var sizeText = sizeLine.Split(';', 2)[0];
                var size = Convert.ToInt32(sizeText, 16);
                if (size == 0)
                {
                    _ = await ReadAsciiLineAsync(stream, cancellationToken);
                    return body.ToArray();
                }

                var chunk = new byte[size];
                await stream.ReadExactlyAsync(chunk, cancellationToken);
                await body.WriteAsync(chunk, cancellationToken);
                _ = await ReadAsciiLineAsync(stream, cancellationToken);
            }
        }

        private static async Task<string> ReadAsciiLineAsync(
            NetworkStream stream,
            CancellationToken cancellationToken)
        {
            var bytes = new List<byte>();
            var buffer = new byte[1];
            while (true)
            {
                if (await stream.ReadAsync(buffer, cancellationToken) == 0)
                {
                    throw new EndOfStreamException("HTTP stream ended before the line completed.");
                }

                bytes.Add(buffer[0]);
                if (bytes.Count >= 2 && bytes[^2] == 13 && bytes[^1] == 10)
                {
                    return Encoding.ASCII.GetString(bytes.Take(bytes.Count - 2).ToArray());
                }
            }
        }

        private static async Task WriteResponseAsync(
            NetworkStream stream,
            int statusCode,
            ReadOnlyMemory<byte> body,
            CancellationToken cancellationToken)
        {
            var reason = statusCode switch
            {
                200 => "OK",
                202 => "Accepted",
                405 => "Method Not Allowed",
                _ => "Error"
            };
            var headers = Encoding.ASCII.GetBytes(
                $"HTTP/1.1 {statusCode} {reason}\r\n" +
                "Content-Type: application/json\r\n" +
                $"Content-Length: {body.Length}\r\n" +
                "Connection: close\r\n\r\n");
            await stream.WriteAsync(headers, cancellationToken);
            if (!body.IsEmpty)
            {
                await stream.WriteAsync(body, cancellationToken);
            }
        }

        private sealed record HttpRequest(string Method, byte[] Body);
    }
}
