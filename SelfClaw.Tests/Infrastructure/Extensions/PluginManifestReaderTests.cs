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

    [Fact]
    public async Task ReadAsync_rejects_dll_entry_points()
    {
        await CreatePackageAsync(ValidManifest.Replace("server/index.js", "server/entry.dll", StringComparison.Ordinal));
        await File.WriteAllTextAsync(Path.Combine(_rootPath, "server", "entry.dll"), "not an assembly");

        var action = () => CreateReader().ReadAsync(Path.Combine(_rootPath, "plugin.json"));

        await action.Should().ThrowAsync<InvalidDataException>().WithMessage("*DLL entry*");
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

    private async Task CreatePackageAsync(string manifest)
    {
        Directory.CreateDirectory(Path.Combine(_rootPath, "instructions"));
        Directory.CreateDirectory(Path.Combine(_rootPath, "skills", "review"));
        Directory.CreateDirectory(Path.Combine(_rootPath, "server"));
        await File.WriteAllTextAsync(Path.Combine(_rootPath, "plugin.json"), manifest);
        await File.WriteAllTextAsync(Path.Combine(_rootPath, "instructions", "direct.md"), "Use office workflows.");
        await File.WriteAllTextAsync(Path.Combine(_rootPath, "skills", "review", "SKILL.md"), "# Review");
        await File.WriteAllTextAsync(Path.Combine(_rootPath, "server", "index.js"), "process.stdin.resume()");
    }

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
