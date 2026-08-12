using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Desktop.Services;
using SelfClaw.Infrastructure.Extensions;
using SelfClaw.Infrastructure.Options;

namespace SelfClaw.Tests.Desktop.Services.Agents;

public sealed class AgentSettingsBridgeTests : IDisposable
{
    private readonly string _rootPath = Path.Combine(
        Path.GetTempPath(),
        "SelfClawTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task GetState_returns_agents_subagents_and_extension_catalog()
    {
        var catalog = CreateSubagentCatalog();
        WriteSubagent(catalog, "reviewer");
        var bridge = CreateBridge();
        using var document = JsonDocument.Parse("{\"requestId\":\"state-request\"}");

        var response = await bridge.TryHandleAsync("agents/get-state", document.RootElement);

        var json = SerializeResponse(response);
        json.GetProperty("requestId").GetString().Should().Be("state-request");
        var state = json.GetProperty("state");
        state.GetProperty("agents")[0].GetProperty("id").GetString().Should().Be("build");
        state.GetProperty("subagents")[0].GetProperty("id").GetString().Should().Be("reviewer");
        state.GetProperty("skills")[0].GetProperty("id").GetString().Should().Be("review");
        state.GetProperty("mcpServers")[0].GetProperty("id").GetString().Should().Be("local");
    }

    [Fact]
    public async Task SaveAgent_updates_basic_info_and_notifies()
    {
        var agentService = CreateAgentService();
        agentService.LoadAll();
        var bridge = CreateBridge(agentService);
        var notifications = 0;
        bridge.AgentsChanged += () => notifications++;
        using var document = JsonDocument.Parse(
            """
            {"requestId":"save-request","id":"build","name":"Builder","description":"构建代理","mode":"cli","instructions":"Be careful."}
            """);

        var response = await bridge.TryHandleAsync("agents/save-agent", document.RootElement);

        var json = SerializeResponse(response);
        json.GetProperty("ok").GetBoolean().Should().BeTrue();
        json.GetProperty("agent").GetProperty("mode").GetString().Should().Be("cli");
        notifications.Should().Be(1);
        json.GetProperty("revision").GetInt64().Should().BeGreaterThan(0);
        var saved = agentService.LoadAll().Single();
        saved.Name.Should().Be("Builder");
        saved.Mode.Should().Be(AgentExecutionMode.Cli);
        saved.Instructions.Should().Be("Be careful.");
    }

    [Fact]
    public async Task SetBinding_writes_agent_markdown()
    {
        var agentService = CreateAgentService();
        agentService.LoadAll();
        var bridge = CreateBridge(agentService);
        using var document = JsonDocument.Parse(
            "{\"requestId\":\"bind-request\",\"agentId\":\"build\",\"kind\":\"skill\",\"id\":\"review\",\"enabled\":true}");

        var response = await bridge.TryHandleAsync("agents/set-binding", document.RootElement);

        var json = SerializeResponse(response);
        json.GetProperty("ok").GetBoolean().Should().BeTrue();
        json.GetProperty("agent").GetProperty("skillIds")[0].GetString().Should().Be("review");
        agentService.LoadAll().Single().SkillIds.Should().Equal("review");
    }

    [Fact]
    public async Task SetBinding_rejects_unknown_extension()
    {
        var bridge = CreateBridge();
        using var document = JsonDocument.Parse(
            "{\"requestId\":\"bind-request\",\"agentId\":\"build\",\"kind\":\"skill\",\"id\":\"missing\",\"enabled\":true}");

        var response = await bridge.TryHandleAsync("agents/set-binding", document.RootElement);

        var json = SerializeResponse(response);
        json.GetProperty("requestId").GetString().Should().Be("bind-request");
        json.GetProperty("error").GetString().Should().Contain("missing");
    }

    [Fact]
    public async Task SetSubagentBinding_updates_agent_allowlist()
    {
        var catalog = CreateSubagentCatalog();
        WriteSubagent(catalog, "reviewer");
        var agentService = CreateAgentService();
        agentService.LoadAll();
        var bridge = CreateBridge(agentService, catalog);
        using var document = JsonDocument.Parse(
            "{\"requestId\":\"allow-request\",\"agentId\":\"build\",\"subagentId\":\"reviewer\",\"enabled\":true}");

        var response = await bridge.TryHandleAsync("agents/set-subagent-binding", document.RootElement);

        SerializeResponse(response).GetProperty("ok").GetBoolean().Should().BeTrue();
        agentService.LoadAll().Single().SubagentIds.Should().Equal("reviewer");
    }

    [Fact]
    public async Task SaveSubagent_persists_definition_changes()
    {
        var catalog = CreateSubagentCatalog();
        WriteSubagent(catalog, "reviewer");
        var bridge = CreateBridge(subagentCatalog: catalog);
        using var document = JsonDocument.Parse(
            """
            {"requestId":"save-sub","id":"reviewer","name":"Reviewer v2","description":"Reviews delegated changes.","toolPolicy":"system","maxRunSeconds":1200,"instructions":"Review strictly."}
            """);

        var response = await bridge.TryHandleAsync("agents/save-subagent", document.RootElement);

        var json = SerializeResponse(response);
        json.GetProperty("ok").GetBoolean().Should().BeTrue();
        json.GetProperty("subagent").GetProperty("toolPolicy").GetString().Should().Be("system");
        var saved = catalog.Get("reviewer");
        saved.Should().NotBeNull();
        saved!.Name.Should().Be("Reviewer v2");
        saved.MaxRunSeconds.Should().Be(1200);
        saved.Instructions.Should().Be("Review strictly.");
    }

    [Theory]
    [InlineData("agents/save-agent", "{\"id\":\"ghost\",\"name\":\"x\",\"mode\":\"direct\"}", "not found")]
    [InlineData("agents/not-supported", "{}", "Unsupported agent message type")]
    public async Task TryHandleAsync_returns_correlated_errors(string type, string payloadJson, string errorFragment)
    {
        var bridge = CreateBridge();
        using var document = JsonDocument.Parse(AddRequestId(payloadJson, "bad-request"));

        var response = await bridge.TryHandleAsync(type, document.RootElement);

        var json = SerializeResponse(response);
        json.GetProperty("requestId").GetString().Should().Be("bad-request");
        json.GetProperty("error").GetString().Should().Contain(errorFragment);
    }

    public void Dispose()
    {
        if (!Directory.Exists(_rootPath))
        {
            return;
        }

        try
        {
            Directory.Delete(_rootPath, true);
        }
        catch (IOException)
        {
        }
    }

    private AgentSettingsBridge CreateBridge(
        DesktopAgentDefinitionService? agentService = null,
        SubagentDefinitionCatalog? subagentCatalog = null)
        => new(
            agentService ?? CreateAgentService(),
            subagentCatalog ?? CreateSubagentCatalog(),
            new StubExtensionSettingsService(),
            new ExtensionStateChangeNotifier());

    private DesktopAgentDefinitionService CreateAgentService()
        => new(CreateStoragePaths());

    private SubagentDefinitionCatalog CreateSubagentCatalog()
        => new(CreateStoragePaths());

    private StoragePaths CreateStoragePaths()
        => new(
            _rootPath,
            Path.Combine(_rootPath, "selfclaw.db"),
            Path.Combine(_rootPath, "secrets"));

    private static void WriteSubagent(SubagentDefinitionCatalog catalog, string id)
    {
        Directory.CreateDirectory(catalog.SubagentsDirectory);
        File.WriteAllText(
            Path.Combine(catalog.SubagentsDirectory, $"{id}.md"),
            """
            ---
            name: Reviewer
            description: Reviews delegated changes.
            ---
            Review only the delegated task.
            """);
    }

    private static JsonElement SerializeResponse(object? response)
    {
        response.Should().NotBeNull();
        return JsonSerializer.SerializeToElement(response, ResponseJsonOptions);
    }

    private static string AddRequestId(string json, string requestId)
    {
        using var document = JsonDocument.Parse(json);
        var values = document.RootElement.EnumerateObject()
            .ToDictionary(property => property.Name, property => property.Value.Clone());
        values["requestId"] = JsonSerializer.SerializeToElement(requestId);
        return JsonSerializer.Serialize(values);
    }

    private static readonly JsonSerializerOptions ResponseJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private sealed class StubExtensionSettingsService : IExtensionSettingsService
    {
        public Task<ExtensionSettingsState> GetStateAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new ExtensionSettingsState(
                1,
                null,
                [],
                [],
                [new ExtensionPackageView(
                    ExtensionKind.Skill,
                    "review",
                    "Review",
                    "1.0.0",
                    "Reviews changes",
                    true,
                    null,
                    [],
                    ExtensionStatus.Ready,
                    [],
                    [])],
                [new McpServerView(
                    "local",
                    "Local",
                    "stdio",
                    true,
                    null,
                    [],
                    ExtensionStatus.Ready,
                    null,
                    [],
                    "node",
                    [],
                    "workspace",
                    true,
                    [],
                    null,
                    null,
                    null,
                    [])]));

        public Task<ExtensionPackageView> ImportPackageAsync(
            ExtensionKind kind,
            string selectedPath,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task SetEnabledAsync(
            ExtensionItemKey key,
            bool enabled,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task AcknowledgePluginPermissionsAsync(
            string id,
            IReadOnlyList<string> permissions,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task DeleteAsync(ExtensionItemKey key, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<McpServerView> SaveMcpServerAsync(
            SaveMcpServerCommand command,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<McpHealthResult> TestMcpServerAsync(
            string id,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
