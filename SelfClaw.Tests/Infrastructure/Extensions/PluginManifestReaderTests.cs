using FluentAssertions;
using SelfClaw.Infrastructure.Extensions.Models;
using SelfClaw.Infrastructure.Extensions.Plugins;

namespace SelfClaw.Tests.Infrastructure.Extensions;

public sealed class PluginManifestReaderTests : IDisposable
{
    private readonly string _rootPath = Path.Combine(
        Path.GetTempPath(), "SelfClawTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ReadAsync_parses_and_validates_contributions()
    {
        await CreatePackageAsync(ValidManifest);

        var manifest = await CreateReader().ReadAsync(Path.Combine(_rootPath, "plugin.json"));

        manifest.Id.Should().Be("office-workflows");
        manifest.Permissions.Should().Equal("process.execute", "workspace.read");
        manifest.Contributions.Skills.Should().ContainSingle().Which.Path.Should().Be("skills/review");
        manifest.Contributions.McpServers.Should().ContainSingle().Which.Arguments[0]
            .Should().Be("${pluginRoot}/server/index.js");
    }

    [Theory]
    [InlineData("../outside.md", "escapes")]
    [InlineData("missing.md", "does not exist")]
    public async Task ReadAsync_rejects_invalid_instruction_paths(string instructionPath, string error)
    {
        await CreatePackageAsync(ValidManifest.Replace("instructions/direct.md", instructionPath, StringComparison.Ordinal));

        var action = () => CreateReader().ReadAsync(Path.Combine(_rootPath, "plugin.json"));

        await action.Should().ThrowAsync<InvalidDataException>().WithMessage($"*{error}*");
    }

    [Fact]
    public async Task ReadAsync_rejects_unknown_template_variables()
    {
        await CreatePackageAsync(ValidManifest.Replace("${pluginRoot}", "${HOME}", StringComparison.Ordinal));

        var action = () => CreateReader().ReadAsync(Path.Combine(_rootPath, "plugin.json"));

        await action.Should().ThrowAsync<InvalidDataException>().WithMessage("*unsupported template*");
    }

    [Theory]
    [InlineData("Office", "*Plugin id*")]
    [InlineData("office_workflows", "*Plugin id*")]
    public async Task ReadAsync_rejects_invalid_plugin_ids(string id, string error)
    {
        await CreatePackageAsync(ValidManifest.Replace("office-workflows", id, StringComparison.Ordinal));

        var action = () => CreateReader().ReadAsync(Path.Combine(_rootPath, "plugin.json"));

        await action.Should().ThrowAsync<InvalidDataException>().WithMessage(error);
    }

    [Fact]
    public async Task ReadAsync_rejects_non_string_mcp_arguments()
    {
        await CreatePackageAsync(CreateMcpManifest(
            "\"transport\":\"stdio\",\"command\":\"node\",\"arguments\":[null]"));

        var action = () => CreateReader().ReadAsync(Path.Combine(_rootPath, "plugin.json"));

        await action.Should().ThrowAsync<InvalidDataException>()
            .WithMessage("*arguments must contain only strings*");
    }

    [Theory]
    [InlineData(
        "\"skills\":[{\"id\":\"review\",\"path\":\"skills/review\"},{\"id\":\"review\",\"path\":\"skills/review\"}],\"mcpServers\":[]",
        "*Duplicate Plugin Skill id*")]
    [InlineData(
        "\"skills\":[],\"mcpServers\":[{\"id\":\"renderer\",\"name\":\"Renderer\",\"transport\":\"stdio\",\"command\":\"node\",\"arguments\":[]},{\"id\":\"renderer\",\"name\":\"Renderer\",\"transport\":\"stdio\",\"command\":\"node\",\"arguments\":[]}]",
        "*Duplicate Plugin MCP id*")]
    public async Task ReadAsync_rejects_duplicate_contribution_ids(string contributions, string error)
    {
        var manifest = $$"""
            {
              "schemaVersion": 1,
              "id": "office-workflows",
              "name": "Office Workflows",
              "version": "1.0.0",
              "permissions": [],
              "contributes": { {{contributions}} }
            }
            """;
        await CreatePackageAsync(manifest);

        var action = () => CreateReader().ReadAsync(Path.Combine(_rootPath, "plugin.json"));

        await action.Should().ThrowAsync<InvalidDataException>().WithMessage(error);
    }

    [Theory]
    [InlineData("server/entry.dll")]
    [InlineData("${pluginRoot}/server/entry.dll")]
    public async Task ReadAsync_rejects_dll_entry_points_with_or_without_the_plugin_root_template(string argument)
    {
        await CreatePackageAsync(ValidManifest.Replace(
            "${pluginRoot}/server/index.js",
            argument,
            StringComparison.Ordinal));
        await File.WriteAllTextAsync(Path.Combine(_rootPath, "server", "entry.dll"), "not an assembly");

        var action = () => CreateReader().ReadAsync(Path.Combine(_rootPath, "plugin.json"));

        await action.Should().ThrowAsync<InvalidDataException>().WithMessage("*DLL entry*");
    }

    [Fact]
    public async Task ReadAsync_allows_a_bare_command_resolved_from_path()
    {
        await CreatePackageAsync(ValidManifest);

        var manifest = await CreateReader().ReadAsync(Path.Combine(_rootPath, "plugin.json"));

        manifest.Contributions.McpServers.Should().ContainSingle().Which.Command.Should().Be("node");
    }

    [Theory]
    [InlineData(
        "\"transport\":\"http\",\"arguments\":[],\"endpoint\":\"http://example.com/mcp\"",
        "*must use HTTPS*")]
    [InlineData(
        "\"transport\":\"http\",\"arguments\":[],\"endpoint\":\"https://example.com/mcp\",\"transportMode\":\"invalid\"",
        "*transportMode is invalid*")]
    [InlineData(
        "\"transport\":\"http\",\"arguments\":[],\"endpoint\":\"https://example.com/mcp\",\"connectionTimeoutSeconds\":301",
        "*connection timeout is invalid*")]
    [InlineData(
        "\"transport\":\"stdio\",\"command\":\"node\",\"arguments\":[],\"requiredSettings\":[{\"key\":\"Authorization\",\"target\":\"header\",\"secret\":true}]",
        "*must target environment variables*")]
    [InlineData(
        "\"transport\":\"http\",\"arguments\":[],\"endpoint\":\"https://example.com/mcp\",\"requiredSettings\":[{\"key\":\"TOKEN\",\"target\":\"env\",\"secret\":true}]",
        "*must target HTTP headers*")]
    [InlineData(
        "\"transport\":\"stdio\",\"command\":\"node\",\"arguments\":[],\"requiredSettings\":[{\"key\":\"TOKEN\",\"target\":\"env\",\"secret\":true},{\"key\":\"token\",\"target\":\"env\",\"secret\":true}]",
        "*contain duplicates*")]
    public async Task ReadAsync_rejects_invalid_mcp_transport_contracts(string mcpJson, string error)
    {
        await CreatePackageAsync(CreateMcpManifest(mcpJson));

        var action = () => CreateReader().ReadAsync(Path.Combine(_rootPath, "plugin.json"));

        await action.Should().ThrowAsync<InvalidDataException>().WithMessage(error);
    }

    public void Dispose()
    {
        if (Directory.Exists(_rootPath)) Directory.Delete(_rootPath, true);
    }

    [Fact]
    public async Task ReadAsync_parses_panel_contributions()
    {
        await CreatePackageAsync(ValidPanelManifest);

        var manifest = await CreateReader().ReadAsync(Path.Combine(_rootPath, "plugin.json"));

        var panel = manifest.Contributions.Panels.Should().ContainSingle().Subject;
        panel.Id.Should().Be("changes");
        panel.Title.Should().Be("变更");
        panel.Icon.Should().Be("git-branch");
        panel.Entry.Should().Be("ui/panel/index.html");
        panel.DefaultWidth.Should().Be(380);
    }

    [Fact]
    public async Task ReadAsync_defaults_the_panel_icon_when_it_is_omitted()
    {
        await CreatePackageAsync(CreatePanelManifest(
            """{"id":"changes","title":"变更","entry":"ui/panel/index.html"}"""));

        var manifest = await CreateReader().ReadAsync(Path.Combine(_rootPath, "plugin.json"));

        manifest.Contributions.Panels.Should().ContainSingle().Which.Icon.Should().Be("puzzle");
    }

    [Theory]
    [InlineData("""{"id":"changes","title":"变更","entry":"../outside.html"}""", "*escapes*")]
    [InlineData("""{"id":"changes","title":"变更","entry":"ui/panel/missing.html"}""", "*entry file does not exist*")]
    [InlineData("""{"id":"changes","title":"变更","entry":"ui/panel/index.txt"}""", "*must be an .html file*")]
    [InlineData("""{"id":"changes","title":"变更"}""", "*must declare an entry*")]
    [InlineData("""{"id":"changes","title":"","entry":"ui/panel/index.html"}""", "*title is invalid*")]
    [InlineData("""{"id":"Changes","title":"变更","entry":"ui/panel/index.html"}""", "*Plugin panel id*")]
    [InlineData("""{"id":"changes","title":"变更","entry":"ui/panel/index.html","icon":"skull"}""", "*not a supported icon*")]
    [InlineData("""{"id":"changes","title":"变更","entry":"ui/panel/index.html","defaultWidth":120}""", "*defaultWidth must be between*")]
    public async Task ReadAsync_rejects_invalid_panels(string panelJson, string error)
    {
        await CreatePackageAsync(CreatePanelManifest(panelJson));

        var action = () => CreateReader().ReadAsync(Path.Combine(_rootPath, "plugin.json"));

        await action.Should().ThrowAsync<InvalidDataException>().WithMessage(error);
    }

    [Fact]
    public async Task ReadAsync_rejects_duplicate_panel_ids()
    {
        await CreatePackageAsync(CreatePanelManifest(
            """{"id":"changes","title":"A","entry":"ui/panel/index.html"},{"id":"changes","title":"B","entry":"ui/panel/index.html"}"""));

        var action = () => CreateReader().ReadAsync(Path.Combine(_rootPath, "plugin.json"));

        await action.Should().ThrowAsync<InvalidDataException>().WithMessage("*Duplicate Plugin panel id*");
    }

    [Fact]
    public async Task ReadAsync_requires_the_panel_permission_when_panels_are_declared()
    {
        await CreatePackageAsync(CreatePanelManifest(
            """{"id":"changes","title":"变更","entry":"ui/panel/index.html"}""",
            permissions: "\"workspace.read\""));

        var action = () => CreateReader().ReadAsync(Path.Combine(_rootPath, "plugin.json"));

        await action.Should().ThrowAsync<InvalidDataException>().WithMessage("*must also declare the 'ui.panel' permission*");
    }

    // These ids are legal package ids but illegal DNS labels, and the panel origin is derived from the
    // id — so they have to fail at install time rather than when a user first opens the tab.
    [Theory]
    [InlineData("office-")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public async Task ReadAsync_rejects_panels_from_a_plugin_id_that_is_not_a_dns_label(string pluginId)
    {
        await CreatePackageAsync(CreatePanelManifest(
                """{"id":"changes","title":"变更","entry":"ui/panel/index.html"}""")
            .Replace("office-workflows", pluginId, StringComparison.Ordinal));

        var action = () => CreateReader().ReadAsync(Path.Combine(_rootPath, "plugin.json"));

        await action.Should().ThrowAsync<InvalidDataException>().WithMessage("*cannot host panels*");
    }

    [Fact]
    public async Task ReadAsync_normalizes_declared_network_origins()
    {
        await CreatePackageAsync(ValidPanelManifest.Replace(
            "network.fetch:https://api.example.com",
            "network.fetch:https://API.Example.com:443/",
            StringComparison.Ordinal));

        var manifest = await CreateReader().ReadAsync(Path.Combine(_rootPath, "plugin.json"));

        manifest.Permissions.Should().Contain("network.fetch:https://api.example.com");
    }

    private async Task CreatePackageAsync(string manifest)
    {
        Directory.CreateDirectory(Path.Combine(_rootPath, "instructions"));
        Directory.CreateDirectory(Path.Combine(_rootPath, "skills", "review"));
        Directory.CreateDirectory(Path.Combine(_rootPath, "server"));
        Directory.CreateDirectory(Path.Combine(_rootPath, "ui", "panel"));
        await File.WriteAllTextAsync(Path.Combine(_rootPath, "plugin.json"), manifest);
        await File.WriteAllTextAsync(Path.Combine(_rootPath, "instructions", "direct.md"), "Use office workflows.");
        await File.WriteAllTextAsync(Path.Combine(_rootPath, "skills", "review", "SKILL.md"), "# Review");
        await File.WriteAllTextAsync(Path.Combine(_rootPath, "server", "index.js"), "process.stdin.resume()");
        await File.WriteAllTextAsync(Path.Combine(_rootPath, "ui", "panel", "index.html"), "<!doctype html>");
        await File.WriteAllTextAsync(Path.Combine(_rootPath, "ui", "panel", "index.txt"), "not a page");
    }

    private static string CreatePanelManifest(string panelJson, string permissions = "\"ui.panel\"")
        => $$"""
            {
              "schemaVersion": 1,
              "id": "office-workflows",
              "name": "Office Workflows",
              "version": "1.0.0",
              "permissions": [{{permissions}}],
              "contributes": { "panels": [{{panelJson}}] }
            }
            """;

    private const string ValidPanelManifest = """
        {
          "schemaVersion": 1,
          "id": "office-workflows",
          "name": "Office Workflows",
          "version": "1.0.0",
          "permissions": ["ui.panel", "host.context.read", "network.fetch:https://api.example.com"],
          "contributes": {
            "panels": [{
              "id": "changes", "title": "变更", "icon": "git-branch",
              "entry": "ui/panel/index.html", "defaultWidth": 380
            }]
          }
        }
        """;

    private static PluginManifestReader CreateReader()
        => new(new ExtensionPackageLimits(1024 * 1024, 4 * 1024 * 1024, 100, 1024 * 1024, 256 * 1024));

    private static string CreateMcpManifest(string mcpJson)
        => $$"""
            {
              "schemaVersion": 1,
              "id": "office-workflows",
              "name": "Office Workflows",
              "version": "1.0.0",
              "permissions": [],
              "contributes": {
                "skills": [],
                "mcpServers": [{"id":"renderer","name":"Renderer",{{mcpJson}}}]
              }
            }
            """;

    private const string ValidManifest = """
        {
          "schemaVersion": 1,
          "id": "office-workflows",
          "name": "Office Workflows",
          "version": "1.0.0",
          "description": "Office tools",
          "permissions": ["workspace.read", "process.execute"],
          "contributes": {
            "directInstructions": "instructions/direct.md",
            "skills": [{ "id": "review", "path": "skills/review" }],
            "mcpServers": [{
              "id": "renderer", "name": "Renderer", "transport": "stdio", "command": "node",
              "arguments": ["${pluginRoot}/server/index.js"], "requiresWorkspace": false, "requiredSettings": []
            }]
          }
        }
        """;
}
