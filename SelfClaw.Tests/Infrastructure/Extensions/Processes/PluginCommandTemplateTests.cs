using FluentAssertions;
using SelfClaw.Infrastructure.Extensions.Processes;

namespace SelfClaw.Tests.Infrastructure.Extensions.Processes;

public sealed class PluginCommandTemplateTests : IDisposable
{
    private readonly string _packageRoot = Path.Combine(
        Path.GetTempPath(), "SelfClawTests", Guid.NewGuid().ToString("N"));

    public PluginCommandTemplateTests()
    {
        Directory.CreateDirectory(_packageRoot);
    }

    [Fact]
    public void Validate_accepts_a_bare_path_command()
        => FluentActions.Invoking(() => PluginCommandTemplate.Validate(_packageRoot, "node", "MCP command"))
            .Should().NotThrow();

    [Fact]
    public void Validate_accepts_an_existing_plugin_relative_entry()
    {
        var script = Path.Combine(_packageRoot, "x.js");
        File.WriteAllText(script, string.Empty);

        FluentActions.Invoking(() => PluginCommandTemplate.Validate(_packageRoot, "${pluginRoot}/x.js", "MCP argument"))
            .Should().NotThrow();
    }

    [Fact]
    public void Validate_rejects_a_missing_plugin_relative_entry()
    {
        var action = () => PluginCommandTemplate.Validate(_packageRoot, "${pluginRoot}/missing.js", "MCP argument");

        action.Should().Throw<InvalidDataException>().WithMessage("*missing package entry*");
    }

    [Fact]
    public void Validate_rejects_an_unknown_template_variable()
    {
        var action = () => PluginCommandTemplate.Validate(_packageRoot, "${home}/x.js", "MCP argument");

        action.Should().Throw<InvalidDataException>().WithMessage("*unsupported template variable*");
    }

    [Theory]
    [InlineData("${pluginRoot}/server.dll")]
    [InlineData("${pluginRoot}/SERVER.DLL")]
    [InlineData("${pluginRoot}/server.dll ")]
    [InlineData("server/entry.dll")]
    public void Validate_rejects_dll_entry_points(string value)
    {
        var action = () => PluginCommandTemplate.Validate(_packageRoot, value, "MCP command");

        action.Should().Throw<InvalidDataException>().WithMessage("*DLL entry points are not supported*");
    }

    [Fact]
    public void Validate_rejects_an_entry_that_escapes_the_package_root()
    {
        var action = () => PluginCommandTemplate.Validate(_packageRoot, "${pluginRoot}/../escape", "MCP argument");

        action.Should().Throw<InvalidDataException>().WithMessage("*escapes the package root*");
    }

    [Fact]
    public void TryExpand_replaces_both_variables()
    {
        var expanded = PluginCommandTemplate.TryExpand(
            "${pluginRoot}/hooks/run.js --root ${workspaceRoot}",
            @"D:\workspace",
            @"D:\plugins\guard");

        expanded.Should().Be(@"D:\plugins\guard/hooks/run.js --root D:\workspace");
    }

    [Fact]
    public void TryExpand_returns_null_when_plugin_root_is_missing()
        => PluginCommandTemplate.TryExpand("${pluginRoot}/x.js", @"D:\workspace", null)
            .Should().BeNull();

    [Fact]
    public void TryExpand_returns_null_when_workspace_root_is_missing()
        => PluginCommandTemplate.TryExpand("${workspaceRoot}/x.js", null, @"D:\plugins\guard")
            .Should().BeNull();

    [Fact]
    public void TryExpand_returns_null_for_a_residual_template_variable()
        => PluginCommandTemplate.TryExpand("${pluginRoot}/${unknown}", @"D:\workspace", @"D:\plugins\guard")
            .Should().BeNull();

    public void Dispose()
    {
        if (Directory.Exists(_packageRoot))
        {
            Directory.Delete(_packageRoot, recursive: true);
        }
    }
}
