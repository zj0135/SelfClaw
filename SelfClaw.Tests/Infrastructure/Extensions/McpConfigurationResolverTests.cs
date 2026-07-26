using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Infrastructure.Extensions;
using SelfClaw.Infrastructure.Extensions.Mcp;
using SelfClaw.Infrastructure.Options;

namespace SelfClaw.Tests.Infrastructure.Extensions;

public sealed class McpConfigurationResolverTests
{
    [Fact]
    public async Task ResolveAsync_OverlaysSecretsWithoutPersistingReferences()
    {
        var protector = new FakeSecretProtector(new Dictionary<string, string>
        {
            ["secret:environment"] = "resolved-env"
        });
        var resolver = CreateResolver(protector);
        var settings = CreateStdioSettings(environment: new Dictionary<string, string>
        {
            ["PLAIN"] = "plain-value"
        });
        var record = CreateRecord(settings, new Dictionary<string, string>
        {
            ["environment.TOKEN"] = "secret:environment"
        });

        var result = await resolver.ResolveAsync(record, "C:\\work");

        result.IsAvailable.Should().BeTrue();
        result.Environment.Should().Contain("PLAIN", "plain-value");
        result.Environment.Should().Contain("TOKEN", "resolved-env");
        result.ToString().Should().NotContain("secret:environment");
    }

    [Fact]
    public async Task ResolveAsync_WhenSecretResolutionFails_ReturnsRedactedReason()
    {
        const string sensitiveReference = "secret:sensitive-reference";
        var resolver = CreateResolver(new ThrowingSecretProtector(sensitiveReference));
        var record = CreateRecord(
            CreateStdioSettings(),
            new Dictionary<string, string> { ["environment.TOKEN"] = sensitiveReference });

        var result = await resolver.ResolveAsync(record, "C:\\work");

        result.IsAvailable.Should().BeFalse();
        result.UnavailableReason.Should().Be("MCP server credentials could not be resolved.");
        result.UnavailableReason.Should().NotContain(sensitiveReference).And.NotContain("TOKEN");
    }

    [Fact]
    public async Task ResolveAsync_WhenWorkspaceIsRequiredWithoutWorkspace_IsUnavailable()
    {
        var resolver = CreateResolver(new FakeSecretProtector());
        var record = CreateRecord(CreateStdioSettings(requiresWorkspace: true));

        var result = await resolver.ResolveAsync(record, null);

        result.IsAvailable.Should().BeFalse();
        result.UnavailableReason.Should().Be("MCP server requires an active workspace.");
    }

    [Fact]
    public async Task ResolveAsync_ResolvesAppDataWorkingDirectory()
    {
        var resolver = CreateResolver(new FakeSecretProtector());
        var record = CreateRecord(CreateStdioSettings(workingDirectoryMode: "appData"));

        var result = await resolver.ResolveAsync(record, null);

        result.IsAvailable.Should().BeTrue();
        result.WorkingDirectory.Should().Be("C:\\SelfClawTest");
    }

    [Fact]
    public async Task ResolveAsync_expands_plugin_and_workspace_templates_without_shell_evaluation()
    {
        var resolver = CreateResolver(new FakeSecretProtector());
        var settings = CreateStdioSettings() with
        {
            Command = "node",
            Arguments = ["${pluginRoot}/server.js", "${workspaceRoot}/$(literal)"] ,
            WorkingDirectoryMode = "plugin"
        };
        var record = CreateRecord(settings) with { SourcePluginId = "office" };

        var result = await resolver.ResolveAsync(record, "C:\\work", "C:\\plugins\\office");

        result.IsAvailable.Should().BeTrue();
        result.Arguments.Should().Equal(
            "C:\\plugins\\office/server.js",
            "C:\\work/$(literal)");
    }

    [Fact]
    public async Task ResolveAsync_required_plugin_setting_is_unavailable_until_configured()
    {
        var resolver = CreateResolver(new FakeSecretProtector());
        var settings = CreateStdioSettings(environment: new Dictionary<string, string>
        {
            ["LICENSE_KEY"] = string.Empty
        }) with { RequiredFieldNames = ["environment.LICENSE_KEY"] };

        var result = await resolver.ResolveAsync(CreateRecord(settings), "C:\\work");

        result.IsAvailable.Should().BeFalse();
        result.UnavailableReason.Should().Be("MCP server requires additional configuration.");
    }

    [Fact]
    public void CreateStdioOptions_UsesWindowsBaselineThenUserOverrides()
    {
        var configuration = new SelfClaw.Infrastructure.Extensions.Mcp.Models.ResolvedMcpServerConfiguration(
            "server",
            "Server",
            McpTransportKind.Stdio,
            1,
            null,
            true,
            null,
            "server.exe",
            [],
            "C:\\work",
            new Dictionary<string, string>
            {
                ["PATH"] = "controlled-user-path",
                ["CUSTOM"] = "value"
            },
            null,
            null,
            null,
            new Dictionary<string, string>(),
            "C:\\work");

        var options = McpTransportFactory.CreateStdioOptions(configuration, new BoundedDiagnosticBuffer());

        options.InheritEnvironmentVariables.Should().BeFalse();
        options.EnvironmentVariables.Should().ContainKeys(
            "SystemRoot", "windir", "ComSpec", "PATHEXT", "TEMP", "TMP", "PATH", "CUSTOM");
        options.EnvironmentVariables["PATH"].Should().Be("controlled-user-path");
        options.EnvironmentVariables["CUSTOM"].Should().Be("value");
    }

    [Fact]
    public void DiagnosticBuffer_CapsContent()
    {
        var buffer = new BoundedDiagnosticBuffer(8);

        buffer.Append("12345");
        buffer.Append("67890");

        buffer.Read().Should().HaveLength(8);
    }

    private static McpConfigurationResolver CreateResolver(ISecretProtector protector)
        => new(protector, new StoragePaths(
            "C:\\SelfClawTest",
            "C:\\SelfClawTest\\selfclaw.db",
            "C:\\SelfClawTest\\secrets"));

    private static McpServerSettings CreateStdioSettings(
        string workingDirectoryMode = "workspace",
        bool requiresWorkspace = false,
        IReadOnlyDictionary<string, string>? environment = null)
        => new(
            "server.exe",
            ["--stdio"],
            workingDirectoryMode,
            requiresWorkspace,
            environment ?? new Dictionary<string, string>(),
            null,
            null,
            null,
            new Dictionary<string, string>(),
            []);

    private static McpServerConfigRecord CreateRecord(
        McpServerSettings settings,
        IReadOnlyDictionary<string, string>? credentialRefs = null)
    {
        var now = DateTimeOffset.UtcNow;
        return new McpServerConfigRecord(
            "server",
            "Server",
            McpTransportKind.Stdio,
            ExtensionCatalog.SerializeSettings(settings),
            credentialRefs ?? new Dictionary<string, string>(),
            null,
            true,
            1,
            [],
            McpServerHealthStatus.Unknown,
            null,
            null,
            now,
            now);
    }

    private sealed class FakeSecretProtector : ISecretProtector
    {
        private readonly IReadOnlyDictionary<string, string> _secrets;

        public FakeSecretProtector(IReadOnlyDictionary<string, string>? secrets = null)
        {
            _secrets = secrets ?? new Dictionary<string, string>();
        }

        public Task<string> StoreSecretAsync(
            string secret,
            string? existingSecretRef = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(existingSecretRef ?? "secret:new");

        public Task<string?> RetrieveSecretAsync(string secretRef, CancellationToken cancellationToken = default)
            => Task.FromResult(_secrets.GetValueOrDefault(secretRef));

        public Task DeleteSecretAsync(string secretRef, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class ThrowingSecretProtector : ISecretProtector
    {
        private readonly string _sensitiveReference;

        public ThrowingSecretProtector(string sensitiveReference)
        {
            _sensitiveReference = sensitiveReference;
        }

        public Task<string> StoreSecretAsync(
            string secret,
            string? existingSecretRef = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<string?> RetrieveSecretAsync(string secretRef, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException($"Could not resolve {_sensitiveReference}.");

        public Task DeleteSecretAsync(string secretRef, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
