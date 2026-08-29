using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using SelfClaw.Core.Interfaces;
using SelfClaw.Core.Models;
using SelfClaw.Desktop.Services;
using SelfClaw.Desktop.Services.Extensions;
using SelfClaw.Desktop.Services.Extensions.Abstractions;
using SelfClaw.Infrastructure.Data.Sqlite;
using SelfClaw.Infrastructure.Data.Sqlite.Repositories;
using SelfClaw.Infrastructure.Extensions;
using SelfClaw.Infrastructure.Options;

namespace SelfClaw.Tests.Desktop.Services.Extensions;

public sealed class ExtensionSettingsBridgeTests : IDisposable
{
    private readonly string _rootPath;

    public ExtensionSettingsBridgeTests()
    {
        _rootPath = Path.Combine(Path.GetTempPath(), "SelfClawTests", Guid.NewGuid().ToString("N"));
    }

    [Theory]
    [InlineData("extensions/get-state", "{}")]
    [InlineData("extensions/list-effective-skills", "{\"agentId\":\"build\"}")]
    [InlineData("extensions/set-enabled", "{\"kind\":\"skill\",\"id\":\"review\",\"enabled\":false}")]
    [InlineData("extensions/acknowledge-plugin-permissions", "{\"id\":\"office\",\"permissions\":[\"workspace.read\"]}")]
    [InlineData("extensions/delete", "{\"kind\":\"skill\",\"id\":\"review\"}")]
    [InlineData("extensions/save-mcp", "{\"name\":\"Local\",\"transport\":\"stdio\",\"command\":\"node\",\"arguments\":[],\"workingDirectoryMode\":\"workspace\",\"environment\":[],\"headers\":[]}")]
    public async Task TryHandleAsync_routes_supported_messages_and_echoes_request_id(
        string type,
        string payloadJson)
    {
        var bridge = CreateBridge(new RecordingExtensionSettingsService());
        using var document = JsonDocument.Parse(AddRequestId(payloadJson, "request-42"));

        var response = await bridge.TryHandleAsync(type, document.RootElement);
        response.Should().NotBeNull();

        var json = SerializeResponse(response);
        json.GetProperty("type").GetString().Should().Be(type);
        json.GetProperty("requestId").GetString().Should().Be("request-42");
        json.TryGetProperty("error", out _).Should().BeFalse();
    }

    [Fact]
    public async Task GetState_merges_agent_bindings_and_never_returns_secret_values()
    {
        var agentService = CreateAgentService();
        agentService.LoadAll();
        agentService.SetExtensionBinding(
            "build",
            new ExtensionItemKey(ExtensionKind.Skill, "review"),
            true);
        var bridge = CreateBridge(new RecordingExtensionSettingsService(), agentService);
        using var document = JsonDocument.Parse("{\"requestId\":\"state-request\"}");

        var response = await bridge.TryHandleAsync("extensions/get-state", document.RootElement, "build");

        var jsonText = JsonSerializer.Serialize(response, ResponseJsonOptions);
        var json = JsonDocument.Parse(jsonText).RootElement;
        var state = json.GetProperty("state");
        state.GetProperty("activeAgentId").GetString().Should().Be("build");
        state.GetProperty("skills")[0].GetProperty("assignedAgentIds")[0].GetString().Should().Be("build");
        state.GetProperty("mcpServers")[0].GetProperty("environment")[0]
            .GetProperty("hasSecret").GetBoolean().Should().BeTrue();
        jsonText.Should().NotContain("plain-secret");
        jsonText.Should().NotContain("secret:token");
    }

    [Fact]
    public async Task ListEffectiveSkills_returns_only_skills_bound_to_the_requested_agent()
    {
        var agentService = CreateAgentService();
        agentService.LoadAll();
        agentService.SetExtensionBinding(
            "build",
            new ExtensionItemKey(ExtensionKind.Skill, "review"),
            true);
        var bridge = CreateBridge(new RecordingExtensionSettingsService(), agentService);
        using var document = JsonDocument.Parse("{\"requestId\":\"skills-request\",\"agentId\":\"build\"}");

        var response = await bridge.TryHandleAsync("extensions/list-effective-skills", document.RootElement);

        var json = SerializeResponse(response);
        json.GetProperty("agentId").GetString().Should().Be("build");
        json.GetProperty("skills").GetArrayLength().Should().Be(1);
        json.GetProperty("skills")[0].GetProperty("id").GetString().Should().Be("review");
    }

    [Theory]
    [InlineData("extensions/not-supported", "{}", "Unsupported extension message type")]
    [InlineData("extensions/set-enabled", "{}", "Property 'kind' is required")]
    public async Task TryHandleAsync_returns_correlated_errors(
        string type,
        string payloadJson,
        string errorFragment)
    {
        var bridge = CreateBridge(new RecordingExtensionSettingsService());
        using var document = JsonDocument.Parse(AddRequestId(payloadJson, "bad-request"));

        var response = await bridge.TryHandleAsync(type, document.RootElement);
        response.Should().NotBeNull();

        var json = SerializeResponse(response);
        json.GetProperty("requestId").GetString().Should().Be("bad-request");
        json.GetProperty("error").GetString().Should().Contain(errorFragment);
    }

    [Fact]
    public async Task TestMcp_returns_health_and_publishes_state_changed()
    {
        var stateChangeNotifier = new ExtensionStateChangeNotifier();
        var bridge = CreateBridge(new RecordingExtensionSettingsService(), stateChangeNotifier: stateChangeNotifier);
        var revisions = new List<long>();
        stateChangeNotifier.StateChanged += revisions.Add;
        using var document = JsonDocument.Parse("{\"requestId\":\"test-request\",\"id\":\"local\"}");

        var response = await bridge.TryHandleAsync("extensions/test-mcp", document.RootElement);

        var json = SerializeResponse(response);
        json.GetProperty("result").GetProperty("status").GetString().Should().Be("ready");
        revisions.Should().ContainSingle();
        json.GetProperty("revision").GetInt64().Should().Be(revisions[0]);
    }

    [Fact]
    public async Task GetState_serializes_extension_status_as_kebab_case_for_the_settings_page()
    {
        var bridge = CreateBridge(new RecordingExtensionSettingsService());
        using var document = JsonDocument.Parse("{}");

        var response = await bridge.TryHandleAsync("extensions/get-state", document.RootElement);

        // MainWindow.PostWebMessage() registers no enum converter, so every other enum reaches the
        // WebView numerically. ExtensionStatus must stay a kebab-case string because the settings badge
        // and the SkillPicker filter branch on those exact values.
        var json = JsonDocument.Parse(JsonSerializer.Serialize(response, HostJsonOptions)).RootElement;
        var state = json.GetProperty("state");
        state.GetProperty("skills")[0].GetProperty("status").GetString().Should().Be("ready");
        state.GetProperty("mcpServers")[0].GetProperty("status").GetString().Should().Be("needs-config");
    }

    [Fact]
    public async Task TryHandleAsync_propagates_caller_cancellation()
    {
        var service = new RecordingExtensionSettingsService { ObserveCancellation = true };
        var bridge = CreateBridge(service);
        using var source = new CancellationTokenSource();
        source.Cancel();
        using var document = JsonDocument.Parse("{}");

        var action = () => bridge.TryHandleAsync(
            "extensions/get-state",
            document.RootElement,
            cancellationToken: source.Token);

        await action.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task ImportPackage_returns_a_correlated_cancelled_result_when_picker_is_cancelled()
    {
        var bridge = CreateBridge(new RecordingExtensionSettingsService());
        using var document = JsonDocument.Parse("{\"requestId\":\"import-request\",\"kind\":\"skill\"}");

        var response = await bridge.TryHandleAsync("extensions/import-package", document.RootElement);

        var json = SerializeResponse(response);
        json.GetProperty("requestId").GetString().Should().Be("import-request");
        json.GetProperty("ok").GetBoolean().Should().BeFalse();
        json.GetProperty("cancelled").GetBoolean().Should().BeTrue();
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

    private ExtensionSettingsBridge CreateBridge(
        IExtensionSettingsService settingsService,
        DesktopAgentDefinitionService? agentDefinitionService = null,
        IExtensionStateChangeNotifier? stateChangeNotifier = null)
    {
        var storagePaths = CreateStoragePaths();
        var repository = new SqliteExtensionRepository(new SqliteDatabase(storagePaths));
        return new ExtensionSettingsBridge(
            settingsService,
            repository,
            agentDefinitionService ?? CreateAgentService(),
            new CancelledPackagePicker(),
            stateChangeNotifier ?? new ExtensionStateChangeNotifier());
    }

    private DesktopAgentDefinitionService CreateAgentService()
        => new(CreateStoragePaths());

    private StoragePaths CreateStoragePaths()
        => new(
            _rootPath,
            Path.Combine(_rootPath, "selfclaw.db"),
            Path.Combine(_rootPath, "secrets"));

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

    /// <summary>
    /// MainWindow.PostWebMessage() serializes with camelCase names and no enum converter, so any enum that
    /// must reach Vue as text has to carry its own [JsonConverter]. Mirroring those exact options here is
    /// what makes the ExtensionStatus wire-format test meaningful.
    /// </summary>
    private static readonly JsonSerializerOptions HostJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private sealed class RecordingExtensionSettingsService : IExtensionSettingsService
    {
        private long _revision = 3;

        public bool ObserveCancellation { get; init; }

        public Task<ExtensionSettingsState> GetStateAsync(CancellationToken cancellationToken = default)
        {
            Observe(cancellationToken);
            return Task.FromResult(new ExtensionSettingsState(
                _revision,
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
                [CreateMcpView()],
                []));
        }

        public Task<ExtensionPackageView> ImportPackageAsync(
            ExtensionKind kind,
            string selectedPath,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task SetEnabledAsync(
            ExtensionItemKey key,
            bool enabled,
            CancellationToken cancellationToken = default)
        {
            Observe(cancellationToken);
            _revision++;
            return Task.CompletedTask;
        }

        public Task AcknowledgePluginPermissionsAsync(
            string id,
            IReadOnlyList<string> permissions,
            CancellationToken cancellationToken = default)
        {
            Observe(cancellationToken);
            _revision++;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(ExtensionItemKey key, CancellationToken cancellationToken = default)
        {
            Observe(cancellationToken);
            _revision++;
            return Task.CompletedTask;
        }

        public Task<McpServerView> SaveMcpServerAsync(
            SaveMcpServerCommand command,
            CancellationToken cancellationToken = default)
        {
            Observe(cancellationToken);
            _revision++;
            return Task.FromResult(CreateMcpView());
        }

        public Task<McpHealthResult> TestMcpServerAsync(
            string id,
            CancellationToken cancellationToken = default)
        {
            Observe(cancellationToken);
            _revision++;
            return Task.FromResult(new McpHealthResult(
                id,
                McpServerHealthStatus.Ready,
                10,
                null,
                ["fixture_echo"]));
        }

        private void Observe(CancellationToken cancellationToken)
        {
            if (ObserveCancellation)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
        }

        private static McpServerView CreateMcpView()
            => new(
                "local",
                "Local",
                "stdio",
                true,
                null,
                [],
                ExtensionStatus.NeedsConfig,
                null,
                [],
                "node",
                [],
                "workspace",
                true,
                [new McpConfigurationEntryView("API_TOKEN", null, true, true)],
                null,
                null,
                null,
                []);
    }

    private sealed class CancelledPackagePicker : IExtensionPackagePicker
    {
        public string? PickPackage(ExtensionKind kind) => null;
    }
}
