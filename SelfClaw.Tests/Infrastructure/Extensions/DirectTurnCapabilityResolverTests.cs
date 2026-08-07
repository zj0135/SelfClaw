using FluentAssertions;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using System.Text.Json;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Infrastructure.Agents.Runtime;
using SelfClaw.Infrastructure.Extensions;
using SelfClaw.Infrastructure.Extensions.Abstractions;
using SelfClaw.Infrastructure.Extensions.Models;
using SelfClaw.Infrastructure.Extensions.Runtime;
using SelfClaw.Infrastructure.Extensions.Skills;
using SelfClaw.Infrastructure.Extensions.Plugins;
using SelfClaw.Infrastructure.Extensions.Mcp;
using SelfClaw.Infrastructure.Extensions.Mcp.Models;
using SelfClaw.Infrastructure.Options;

namespace SelfClaw.Tests.Infrastructure.Extensions;

public sealed class DirectTurnCapabilityResolverTests : IDisposable
{
    private readonly string _rootPath = Path.Combine(
        Path.GetTempPath(),
        "SelfClawTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ResolveAsync_intersects_bindings_and_state_then_activates_and_strips_tokens()
    {
        var review = await CreatePackageAsync("review", enabled: true);
        var unbound = await CreatePackageAsync("unbound", enabled: true);
        var disabled = await CreatePackageAsync("disabled", enabled: false);
        var repository = new PackageRepository([review, unbound, disabled]);
        var resolver = CreateResolver(repository);
        var request = CreateRequest(
            ["review", "disabled"],
            "run [/review] and [/review]",
            "history [/review] [/unknown]");

        await using var lease = await resolver.ResolveAsync(request);

        lease.SystemInstructions.Should().HaveCount(3);
        lease.SystemInstructions[0].Should().StartWith("[SelfClaw Capability Policy]");
        lease.SystemInstructions[1].Should().Contain("BEGIN SELFCLAW SKILL review").And.Contain("# review instructions");
        lease.SystemInstructions[2].Should().Contain("SelfClaw Available Skills");
        lease.Tools.Select(tool => tool.Name).Should().Equal("activate_skill", "read_skill_resource");
        lease.Diagnostics.Should().ContainSingle(item => item.Contains("activated Skill 'review'"));
        lease.MessageAdjustments[request.Messages[0].Id].Should().Be("history  [/unknown]");
        lease.MessageAdjustments[request.Messages[1].Id].Should().Be("run  and ");
        request.Messages[1].MarkdownContent.Should().Be("run [/review] and [/review]");
    }

    [Fact]
    public async Task ResolveAsync_uses_explicit_token_order_and_allows_three_unique_skills()
    {
        var first = await CreatePackageAsync("first", true);
        var second = await CreatePackageAsync("second", true);
        var third = await CreatePackageAsync("third", true);
        var resolver = CreateResolver(new PackageRepository([first, second, third]));
        var request = CreateRequest(
            ["first", "second", "third"],
            "[/second] [/first] [/third]");

        await using var lease = await resolver.ResolveAsync(request);

        lease.SystemInstructions[1].Should().Contain("SKILL second");
        lease.SystemInstructions[2].Should().Contain("SKILL first");
        lease.SystemInstructions[3].Should().Contain("SKILL third");
        lease.SystemInstructions[4].Should().Contain("SelfClaw Available Skills");
    }

    [Fact]
    public async Task ResolveAsync_does_not_expand_an_empty_agent_binding()
    {
        var package = await CreatePackageAsync("review", true);
        var resolver = CreateResolver(new PackageRepository([package]));

        await using var lease = await resolver.ResolveAsync(CreateRequest([], "plain prompt"));

        lease.SystemInstructions.Should().BeEmpty();
        lease.MessageAdjustments.Should().BeEmpty();
    }

    [Theory]
    [InlineData("read-only", 4)]
    [InlineData("none", 0)]
    public async Task ResolveAsync_applies_child_tool_policy_and_never_adds_delegation_tools(
        string toolPolicy,
        int expectedToolCount)
    {
        var resolver = CreateResolver(new PackageRepository([]));
        var now = DateTimeOffset.UtcNow;
        var request = CreateRequest([], "isolated task") with
        {
            WorkspaceRoot = new WorkspaceRoot(Guid.NewGuid(), "Workspace", _rootPath, now, now),
            Agent = CreateRequest([], "isolated task").Agent with
            {
                ToolPolicy = toolPolicy,
                SubagentIds = ["nested"]
            },
            ExecutionContext = new DirectTurnExecutionContext(
                DirectTurnOrigin.Subagent,
                new DirectCapabilityCeiling(
                    AgentRuntimeDefinition.SystemToolPolicy,
                    [],
                    [],
                    [],
                    ["nested"]),
                null)
        };

        await using var lease = await resolver.ResolveAsync(request);

        lease.Tools.Should().HaveCount(expectedToolCount);
        lease.Tools.Select(tool => tool.Name).Should().NotContain(name => name.Contains("subagent"));
        if (toolPolicy == "read-only")
        {
            lease.Tools.Select(tool => tool.Name).Should().Equal(
                "list_files",
                "glob_files",
                "search_text",
                "read_file");
        }
    }

    [Fact]
    public async Task ResolveAsync_expands_acknowledged_plugin_instructions_and_namespaced_skills()
    {
        var limits = CreateLimits();
        var pluginRoot = Path.Combine(_rootPath, "plugins", "office", "versions", "v1");
        Directory.CreateDirectory(Path.Combine(pluginRoot, "instructions"));
        Directory.CreateDirectory(Path.Combine(pluginRoot, "skills", "slides"));
        await File.WriteAllTextAsync(Path.Combine(pluginRoot, "instructions", "direct.md"), "Use the office workflow.");
        await File.WriteAllTextAsync(Path.Combine(pluginRoot, "skills", "slides", "SKILL.md"), """
            ---
            name: slides
            description: Build slides
            version: 1.0.0
            ---
            # Slide instructions
            """);
        var manifestJson = """
            {
              "schemaVersion": 1,
              "id": "office",
              "name": "Office",
              "version": "1.0.0",
              "permissions": ["workspace.read"],
              "contributes": {
                "directInstructions": "instructions/direct.md",
                "skills": [{ "id": "slides", "path": "skills/slides" }],
                "mcpServers": []
              }
            }
            """;
        await File.WriteAllTextAsync(Path.Combine(pluginRoot, "plugin.json"), manifestJson);
        var now = DateTimeOffset.UtcNow;
        var plugin = new ExtensionPackageRecord(
            ExtensionKind.Plugin, "office", "Office", "1.0.0", "", pluginRoot,
            "sha256:office", manifestJson, null, true, "[\"workspace.read\"]", now, now, now);
        var versionLeases = new PluginVersionLeaseManager();
        var resolver = CreateResolver(
            new PackageRepository([plugin]),
            new PluginManifestReader(limits),
            versionLeases);
        var request = CreateRequest([], "[/office/slides]", pluginIds: ["office"]);

        var lease = await resolver.ResolveAsync(request);

        lease.SystemInstructions[0].Should().Contain("Capability Policy");
        lease.SystemInstructions[1].Should().Contain("BEGIN SELFCLAW PLUGIN office");
        lease.SystemInstructions[2].Should().Contain("BEGIN SELFCLAW SKILL office/slides");
        lease.Tools.Select(tool => tool.Name).Should().Equal("activate_skill", "read_skill_resource");
        lease.MessageAdjustments[request.Messages.Single().Id].Should().BeEmpty();
        var drainTask = versionLeases.DrainAsync(pluginRoot);
        drainTask.IsCompleted.Should().BeFalse();
        await lease.DisposeAsync();
        await drainTask;
    }

    [Theory]
    [InlineData("[/missing]", "not installed")]
    [InlineData("[/disabled]", "disabled")]
    [InlineData("[/unbound]", "not bound")]
    [InlineData("[/broken]", "broken")]
    [InlineData("[/Upper]", "invalid")]
    [InlineData("[/a/b/c]", "invalid")]
    public async Task ResolveAsync_rejects_invalid_or_unavailable_explicit_skills(
        string prompt,
        string errorFragment)
    {
        var disabled = await CreatePackageAsync("disabled", false);
        var unbound = await CreatePackageAsync("unbound", true);
        var broken = CreatePackageRecord("broken", true, Path.Combine(_rootPath, "missing"));
        var resolver = CreateResolver(new PackageRepository([disabled, unbound, broken]));
        var request = CreateRequest(["disabled", "broken"], prompt);

        var action = () => resolver.ResolveAsync(request);

        await action.Should().ThrowAsync<InvalidDataException>().WithMessage($"*{errorFragment}*");
    }

    [Fact]
    public async Task ResolveAsync_rejects_more_than_three_unique_explicit_skills()
    {
        var packages = new[]
        {
            await CreatePackageAsync("one", true),
            await CreatePackageAsync("two", true),
            await CreatePackageAsync("three", true),
            await CreatePackageAsync("four", true)
        };
        var resolver = CreateResolver(new PackageRepository(packages));
        var request = CreateRequest(packages.Select(package => package.Id).ToArray(),
            "[/one] [/two] [/three] [/four]");

        var action = () => resolver.ResolveAsync(request);

        await action.Should().ThrowAsync<InvalidDataException>().WithMessage("*at most 3*");
    }

    [Fact]
    public async Task ResolveAsync_mcp_failure_does_not_hide_healthy_server_and_health_changes_publish_revision()
    {
        var servers = new McpRepository([CreateMcpRecord("failing"), CreateMcpRecord("healthy")]);
        var manager = new RecordingMcpClientManager("failing");
        var notifier = new ExtensionStateChangeNotifier();
        var revisions = new List<long>();
        notifier.StateChanged += revisions.Add;
        var resolver = CreateResolver(
            new PackageRepository([]),
            CreateMcpSource(servers, manager, notifier));
        var request = CreateRequest([], "plain prompt", mcpServerIds: ["failing", "healthy"]);

        await using var lease = await resolver.ResolveAsync(request);

        lease.Diagnostics.Should().Contain(item => item.Contains("MCP server 'failing' was skipped"));
        lease.SystemInstructions.Should().Contain(section =>
            section.Contains("Capability Degradation") && section.Contains("'failing' was skipped"));
        servers.Records["failing"].LastStatus.Should().Be(McpServerHealthStatus.Degraded);
        servers.Records["healthy"].LastStatus.Should().Be(McpServerHealthStatus.Ready);
        revisions.Should().Equal(1, 2);
        await lease.DisposeAsync();
        manager.ReleasedLeaseCount.Should().Be(1);
    }

    [Fact]
    public async Task ResolveAsync_rejects_mcp_tool_names_that_collide_after_provider_name_normalization()
    {
        var servers = new McpRepository([CreateMcpRecord("fixture")]);
        var manager = new RecordingMcpClientManager(
            string.Empty,
            [CreateMcpTool("a.b"), CreateMcpTool("a_b")]);
        var resolver = CreateResolver(new PackageRepository([]), CreateMcpSource(servers, manager));

        var action = () => resolver.ResolveAsync(
            CreateRequest([], "plain prompt", mcpServerIds: ["fixture"]));

        await action.Should().ThrowAsync<InvalidDataException>()
            .WithMessage("*MCP tool name collision*mcp__fixture__a_b*");
        manager.ReleasedLeaseCount.Should().Be(1);
    }

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, true);
        }
    }

    private DirectTurnCapabilityResolver CreateResolver(
        IExtensionPackageRepository repository,
        PluginManifestReader? pluginManifestReader = null,
        PluginVersionLeaseManager? pluginVersionLeaseManager = null)
        => CreateResolver(
            repository,
            CreateMcpSource(new McpRepository([]), new RecordingMcpClientManager(string.Empty)),
            pluginManifestReader,
            pluginVersionLeaseManager);

    private DirectTurnCapabilityResolver CreateResolver(
        IExtensionPackageRepository repository,
        McpCapabilitySource mcpSource,
        PluginManifestReader? pluginManifestReader = null,
        PluginVersionLeaseManager? pluginVersionLeaseManager = null)
    {
        var limits = CreateLimits();
        return new DirectTurnCapabilityResolver(
            new WorkspaceAgentToolset(new NoOpWorkspaceTools()),
            repository,
            new SkillCapabilitySource(
                new SkillPackageReader(limits),
                new SkillTokenParser(),
                new SkillRuntimeToolset()),
            new PluginCapabilitySource(
                pluginManifestReader ?? new PluginManifestReader(limits),
                new SkillPackageReader(limits),
                pluginVersionLeaseManager ?? new PluginVersionLeaseManager()),
            mcpSource,
            new SelfClaw.Infrastructure.Agents.Subagents.Runtime.SubagentCapabilitySource(null));
    }

    private McpCapabilitySource CreateMcpSource(
        McpRepository servers,
        IMcpClientManager clientManager,
        IExtensionStateChangeNotifier? stateChangeNotifier = null)
        => new(
            servers,
            new McpConfigurationResolver(
                new NoOpSecretProtector(),
                new StoragePaths(
                    _rootPath,
                    Path.Combine(_rootPath, "selfclaw.db"),
                    Path.Combine(_rootPath, "secrets"))),
            clientManager,
            new McpToolAdapter(),
            stateChangeNotifier ?? new ExtensionStateChangeNotifier());

    private static ExtensionPackageLimits CreateLimits()
        => new(1024 * 1024, 1024 * 1024, 100, 512 * 1024, 256 * 1024);

    private async Task<ExtensionPackageRecord> CreatePackageAsync(string id, bool enabled)
    {
        var installPath = Path.Combine(_rootPath, "skills", id);
        Directory.CreateDirectory(installPath);
        await File.WriteAllTextAsync(Path.Combine(installPath, "SKILL.md"), $"""
            ---
            name: {id}
            description: {id} description
            version: 1.0.0
            ---
            # {id} instructions
            """);
        return CreatePackageRecord(id, enabled, installPath);
    }

    private static ExtensionPackageRecord CreatePackageRecord(string id, bool enabled, string installPath)
    {
        var now = DateTimeOffset.UtcNow;
        return new ExtensionPackageRecord(
            ExtensionKind.Skill,
            id,
            id,
            "1.0.0",
            $"{id} description",
            installPath,
            $"sha256:{id}",
            "{}",
            null,
            enabled,
            null,
            null,
            now,
            now);
    }

    private static DirectChatTurnRequest CreateRequest(
        IReadOnlyList<string> skillIds,
        string latestPrompt,
        string? historicalPrompt = null,
        IReadOnlyList<string>? pluginIds = null,
        IReadOnlyList<string>? mcpServerIds = null)
    {
        var conversationId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var messages = new List<MessageRecord>();
        if (historicalPrompt is not null)
        {
            messages.Add(new MessageRecord(
                Guid.NewGuid(), conversationId, MessageRole.User, historicalPrompt,
                MessageStatus.Completed, now, now));
        }

        messages.Add(new MessageRecord(
            Guid.NewGuid(), conversationId, MessageRole.User, latestPrompt,
            MessageStatus.Completed, now, now));
        return new DirectChatTurnRequest(
            Guid.NewGuid(),
            conversationId,
            null,
            new AgentRuntimeDefinition(
                "build", "Build", "", AgentExecutionMode.Direct,
                AgentRuntimeDefinition.SystemToolPolicy, pluginIds ?? [], skillIds, mcpServerIds ?? [], [], "Agent instructions"),
            messages,
            Guid.NewGuid(),
            ToolPermissionMode.FullAccess,
            null,
            new DirectTurnExecutionContext(DirectTurnOrigin.Interactive, null, null));
    }

    private static McpServerConfigRecord CreateMcpRecord(string id)
    {
        var now = DateTimeOffset.UtcNow;
        return new McpServerConfigRecord(
            id,
            id,
            McpTransportKind.Stdio,
            ExtensionCatalog.SerializeSettings(new McpServerSettings(
                "server.exe",
                [],
                "appData",
                false,
                new Dictionary<string, string>(),
                null,
                null,
                null,
                new Dictionary<string, string>(),
                [])),
            new Dictionary<string, string>(),
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

    private static McpClientTool CreateMcpTool(string name)
    {
        var protocolTool = new Tool
        {
            Name = name,
            Description = $"Fixture tool {name}",
            InputSchema = JsonSerializer.SerializeToElement(new
            {
                type = "object",
                properties = new { }
            })
        };
        return new McpClientTool(new FixtureMcpClient(), protocolTool);
    }

#pragma warning disable MCPEXP002
    private sealed class FixtureMcpClient : McpClient
    {
        public override ServerCapabilities ServerCapabilities => throw new NotSupportedException();

        public override Implementation ServerInfo => throw new NotSupportedException();

        public override string? ServerInstructions => null;

        public override Task<ClientCompletionDetails> Completion => throw new NotSupportedException();

        public override string? SessionId => null;

        public override string? NegotiatedProtocolVersion => null;

        public override Task<JsonRpcResponse> SendRequestAsync(
            JsonRpcRequest request,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public override Task SendMessageAsync(
            JsonRpcMessage message,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public override IAsyncDisposable RegisterNotificationHandler(
            string method,
            Func<JsonRpcNotification, CancellationToken, ValueTask> handler)
            => throw new NotSupportedException();

        public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
#pragma warning restore MCPEXP002

    private sealed class PackageRepository : IExtensionPackageRepository
    {
        private readonly IReadOnlyList<ExtensionPackageRecord> _packages;

        public PackageRepository(IReadOnlyList<ExtensionPackageRecord> packages)
        {
            _packages = packages;
        }

        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<ExtensionPackageRecord>> ListPackagesAsync(CancellationToken cancellationToken = default) => Task.FromResult(_packages);
        public Task<ExtensionPackageRecord?> GetPackageAsync(ExtensionKind kind, string id, CancellationToken cancellationToken = default) => Task.FromResult(_packages.FirstOrDefault(package => package.Kind == kind && package.Id == id));
        public Task<ExtensionPackageRecord> UpsertPackageAsync(ExtensionPackageRecord package, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetPackageEnabledAsync(ExtensionKind kind, string id, bool enabled, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeletePackageAsync(ExtensionKind kind, string id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class McpRepository : IMcpServerRepository
    {
        public McpRepository(IEnumerable<McpServerConfigRecord> records)
        {
            Records = records.ToDictionary(record => record.Id, StringComparer.OrdinalIgnoreCase);
        }

        public Dictionary<string, McpServerConfigRecord> Records { get; }

        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<McpServerConfigRecord>> ListMcpServersAsync(
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<McpServerConfigRecord>>(Records.Values.ToArray());

        public Task<McpServerConfigRecord?> GetMcpServerAsync(
            string id,
            CancellationToken cancellationToken = default)
            => Task.FromResult(Records.GetValueOrDefault(id));

        public Task<McpServerConfigRecord> UpsertMcpServerAsync(
            McpServerConfigRecord server,
            CancellationToken cancellationToken = default)
        {
            Records[server.Id] = server;
            return Task.FromResult(server);
        }

        public Task SetMcpServerEnabledAsync(
            string id,
            bool enabled,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task DeleteMcpServerAsync(string id, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class RecordingMcpClientManager : IMcpClientManager
    {
        private readonly string _failingId;
        private readonly IReadOnlyList<McpClientTool> _tools;

        public RecordingMcpClientManager(
            string failingId,
            IReadOnlyList<McpClientTool>? tools = null)
        {
            _failingId = failingId;
            _tools = tools ?? [];
        }

        public int ReleasedLeaseCount { get; private set; }

        public Task<McpClientLease> AcquireAsync(
            ResolvedMcpServerConfiguration configuration,
            CancellationToken cancellationToken = default)
            => string.Equals(configuration.Id, _failingId, StringComparison.Ordinal)
                ? throw new InvalidOperationException("fixture connection failure")
                : Task.FromResult(new McpClientLease(_tools, () =>
                {
                    ReleasedLeaseCount++;
                    return ValueTask.CompletedTask;
                }));

        public Task<McpHealthResult> TestAsync(
            ResolvedMcpServerConfiguration configuration,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task DrainAsync(string serverId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class NoOpSecretProtector : ISecretProtector
    {
        public Task<string> StoreSecretAsync(
            string secret,
            string? existingSecretRef = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<string?> RetrieveSecretAsync(
            string secretRef,
            CancellationToken cancellationToken = default)
            => Task.FromResult<string?>(null);

        public Task DeleteSecretAsync(string secretRef, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class NoOpWorkspaceTools : IWorkspaceToolService
    {
        public Task<IReadOnlyList<WorkspaceFileEntry>> ListFilesAsync(string root, string? path, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<WorkspaceFileEntry>> GlobFilesAsync(string root, string pattern, string? path = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<WorkspaceSearchHit>> SearchTextAsync(string root, string query, WorkspaceSearchOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<WorkspaceFileContent> ReadFileAsync(string root, string path, int? startLine = null, int? lineCount = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<WorkspaceFileWriteResult> WriteFileAsync(string root, string path, string content, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<WorkspaceFileWriteResult> EditFileAsync(string root, string path, string oldText, string newText, bool replaceAll = false, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ShellCommandResult> RunShellCommandAsync(string root, string command, int timeout, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
