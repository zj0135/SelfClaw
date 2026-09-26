using FluentAssertions;
using SelfClaw.Core.Runtime;
using SelfClaw.Core.Runtime.Agent;
using SelfClaw.Infrastructure.Extensions.Models;
using SelfClaw.Infrastructure.Extensions.Plugins;
using SelfClaw.Infrastructure.Extensions.Plugins.Models;

namespace SelfClaw.Tests.Infrastructure.Extensions.Plugins;

public sealed class PluginManifestReaderHookTests : IDisposable
{
    private const string AllHookPermissions =
        "\"hooks.run\",\"hooks.tool\",\"hooks.http\",\"hooks.http.body\"";

    private readonly string _rootPath = Path.Combine(
        Path.GetTempPath(), "SelfClawTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ReadAsync_parses_hook_contributions()
    {
        await CreatePackageAsync(ValidHookManifest);

        var manifest = await CreateReader().ReadAsync(Path.Combine(_rootPath, "plugin.json"));

        manifest.Contributions.Hooks.Should().HaveCount(2);
        var shellGuard = manifest.Contributions.Hooks[0];
        shellGuard.Id.Should().Be("deny-dangerous-shell");
        shellGuard.Event.Should().Be(PluginHookEvent.ToolExecuting);
        shellGuard.Matcher.ToolPatterns.Should().Equal("run_shell_command", "mcp__github__*");
        shellGuard.Command.Should().Be("powershell.exe");
        shellGuard.Arguments.Should().Equal(
            "-NoProfile",
            "-NonInteractive",
            "-ExecutionPolicy",
            "Bypass",
            "-File",
            "${pluginRoot}/hooks/guard.ps1");
        shellGuard.Timeout.Should().Be(TimeSpan.FromSeconds(10));
        shellGuard.OnFailure.Should().Be(PluginHookFailurePolicy.Block);
        shellGuard.RunAsync.Should().BeFalse();
        shellGuard.IncludeRequestBody.Should().BeFalse();

        var audit = manifest.Contributions.Hooks[1];
        audit.Event.Should().Be(PluginHookEvent.RunCompleted);
        audit.Matcher.Origins.Should().Equal(DirectTurnOrigin.Interactive, DirectTurnOrigin.Subagent);
        audit.Timeout.Should().Be(TimeSpan.FromSeconds(30));
        audit.OnFailure.Should().Be(PluginHookFailurePolicy.Continue);
    }

    [Fact]
    public async Task ReadAsync_defaults_timeouts_per_event_and_async()
    {
        var manifest = CreateManifest(
            """
            { "id": "a", "event": "toolExecuting", "command": "node" },
            { "id": "b", "event": "httpRequestSending", "command": "node" },
            { "id": "c", "event": "runCompleted", "command": "node" },
            { "id": "d", "event": "toolExecuted", "command": "node", "async": true }
            """,
            AllHookPermissions);
        await CreatePackageAsync(manifest);

        var hooks = (await CreateReader().ReadAsync(Path.Combine(_rootPath, "plugin.json"))).Contributions.Hooks;

        hooks[0].Timeout.Should().Be(TimeSpan.FromSeconds(10));
        hooks[1].Timeout.Should().Be(TimeSpan.FromSeconds(5));
        hooks[2].Timeout.Should().Be(TimeSpan.FromSeconds(30));
        hooks[3].Timeout.Should().Be(TimeSpan.FromSeconds(30));
        hooks[3].RunAsync.Should().BeTrue();
    }

    [Theory]
    [InlineData("""{ "id": "x", "event": "RunStarting", "command": "node" }""", "*event is invalid*")]
    [InlineData("""{ "id": "x", "event": "toolExecuting", "command": "" }""", "*must declare a command*")]
    [InlineData("""{ "id": "x", "event": "toolExecuting", "command": "hooks/guard.exe" }""", "*must be a bare executable name*")]
    [InlineData("""{ "id": "x", "event": "toolExecuting", "command": "node", "arguments": [null] }""", "*arguments must contain only strings*")]
    [InlineData("""{ "id": "x", "event": "toolExecuting", "command": "node", "matcher": { "tools": [] } }""", "*tools must not be empty*")]
    [InlineData("""{ "id": "x", "event": "runStarting", "command": "node", "matcher": { "sourceIds": ["a"] } }""", "*only valid on tool events*")]
    [InlineData("""{ "id": "x", "event": "toolExecuting", "command": "node", "matcher": { "hosts": ["example.com"] } }""", "*only valid on HTTP events*")]
    [InlineData("""{ "id": "x", "event": "runStarting", "command": "node", "matcher": { "origins": [] } }""", "*origins must not be empty*")]
    [InlineData("""{ "id": "x", "event": "runStarting", "command": "node", "matcher": { "origins": ["Interactive"] } }""", "*origins contains an invalid value*")]
    [InlineData("""{ "id": "x", "event": "toolExecuting", "command": "node", "matcher": { "kinds": ["nope"] } }""", "*kinds contains an invalid value*")]
    [InlineData("""{ "id": "x", "event": "toolExecuting", "command": "node", "matcher": { "sources": ["builtin"] } }""", "*sources contains an invalid value*")]
    [InlineData("""{ "id": "x", "event": "toolExecuting", "command": "node", "matcher": { "tools": ["bad name"] } }""", "*tools contains an invalid value*")]
    [InlineData("""{ "id": "x", "event": "toolExecuted", "command": "node", "onFailure": "block" }""", "*may only declare onFailure*")]
    [InlineData("""{ "id": "x", "event": "runCompleted", "command": "node", "async": true }""", "*may only declare async*")]
    [InlineData("""{ "id": "x", "event": "toolExecuted", "command": "node", "includeRequestBody": true }""", "*may only declare includeRequestBody*")]
    [InlineData("""{ "id": "x", "event": "toolExecuting", "command": "node", "timeoutSeconds": 0 }""", "*timeoutSeconds must be between 1 and 60*")]
    [InlineData("""{ "id": "x", "event": "toolExecuting", "command": "node", "timeoutSeconds": 61 }""", "*timeoutSeconds must be between 1 and 60*")]
    [InlineData("""{ "id": "x", "event": "httpRequestSending", "command": "node", "timeoutSeconds": 31 }""", "*timeoutSeconds must be between 1 and 30*")]
    [InlineData("""{ "id": "x", "event": "runCompleted", "command": "node", "timeoutSeconds": 121 }""", "*timeoutSeconds must be between 1 and 120*")]
    [InlineData("""{ "id": "x", "event": "toolExecuted", "command": "node", "async": true, "timeoutSeconds": 121 }""", "*timeoutSeconds must be between 1 and 120*")]
    [InlineData("""{ "id": "x", "event": "toolExecuting", "command": "node", "onFailure": "deny" }""", "*onFailure must be continue or block*")]
    public async Task ReadAsync_rejects_invalid_hooks(string hookJson, string error)
    {
        await CreatePackageAsync(CreateManifest(hookJson, AllHookPermissions));

        var action = () => CreateReader().ReadAsync(Path.Combine(_rootPath, "plugin.json"));

        await action.Should().ThrowAsync<InvalidDataException>().WithMessage(error);
    }

    [Fact]
    public async Task ReadAsync_rejects_includeRequestBody_without_the_body_permission()
    {
        await CreatePackageAsync(CreateManifest(
            """{ "id": "x", "event": "httpRequestSending", "command": "node", "includeRequestBody": true }""",
            "\"hooks.http\""));

        var action = () => CreateReader().ReadAsync(Path.Combine(_rootPath, "plugin.json"));

        await action.Should().ThrowAsync<InvalidDataException>()
            .WithMessage("*requires the 'hooks.http.body' permission*");
    }

    [Fact]
    public async Task ReadAsync_rejects_a_hook_without_its_event_permission()
    {
        await CreatePackageAsync(CreateManifest(
            """{ "id": "x", "event": "toolExecuting", "command": "node" }""",
            "\"hooks.run\""));

        var action = () => CreateReader().ReadAsync(Path.Combine(_rootPath, "plugin.json"));

        await action.Should().ThrowAsync<InvalidDataException>().WithMessage("*requires the 'hooks.tool' permission*");
    }

    [Fact]
    public async Task ReadAsync_rejects_more_than_32_hooks()
    {
        var hooks = string.Join(",\n", Enumerable.Range(0, 33).Select(index =>
            $$"""{ "id": "hook-{{index}}", "event": "runStarting", "command": "node" }"""));
        await CreatePackageAsync(CreateManifest(hooks, AllHookPermissions));

        var action = () => CreateReader().ReadAsync(Path.Combine(_rootPath, "plugin.json"));

        await action.Should().ThrowAsync<InvalidDataException>().WithMessage("*more than 32 hooks*");
    }

    [Fact]
    public async Task ReadAsync_rejects_duplicate_hook_ids()
    {
        await CreatePackageAsync(CreateManifest(
            """
            { "id": "dup", "event": "runStarting", "command": "node" },
            { "id": "dup", "event": "runCompleted", "command": "node" }
            """,
            AllHookPermissions));

        var action = () => CreateReader().ReadAsync(Path.Combine(_rootPath, "plugin.json"));

        await action.Should().ThrowAsync<InvalidDataException>().WithMessage("*Duplicate Plugin hook id 'dup'*");
    }

    [Theory]
    [InlineData("\"127.0.0.1\"", "127.0.0.1")]
    [InlineData("\"127.1\"", "127.0.0.1")]
    [InlineData("\"0x7f.0.0.1\"", "127.0.0.1")]
    [InlineData("\"::1\"", "::1")]
    [InlineData("\"0:0:0:0:0:0:0:1\"", "::1")]
    [InlineData("\"*.openai.azure.com\"", "*.openai.azure.com")]
    [InlineData("\"API.Example.com\"", "api.example.com")]
    public async Task ReadAsync_accepts_and_canonicalizes_valid_hosts(string hostJson, string expected)
    {
        await CreatePackageAsync(CreateManifest(
            $$"""{ "id": "x", "event": "httpRequestSending", "command": "node", "matcher": { "hosts": [{{hostJson}}] } }""",
            AllHookPermissions));

        var hooks = (await CreateReader().ReadAsync(Path.Combine(_rootPath, "plugin.json"))).Contributions.Hooks;

        hooks.Single().Matcher.HostPatterns.Should().Equal(expected);
    }

    [Theory]
    [InlineData("\"*.127.0.0.1\"")]
    [InlineData("\"*.::1\"")]
    [InlineData("\"example.*.com\"")]
    [InlineData("\"*\"")]
    [InlineData("\"[::1]\"")]
    [InlineData("\"[2001:db8::1]\"")]
    [InlineData("\"fe80::1%eth0\"")]
    public async Task ReadAsync_rejects_invalid_hosts(string hostJson)
    {
        await CreatePackageAsync(CreateManifest(
            $$"""{ "id": "x", "event": "httpRequestSending", "command": "node", "matcher": { "hosts": [{{hostJson}}] } }""",
            AllHookPermissions));

        var action = () => CreateReader().ReadAsync(Path.Combine(_rootPath, "plugin.json"));

        await action.Should().ThrowAsync<InvalidDataException>().WithMessage("*hosts contains an invalid value*");
    }

    [Fact]
    public async Task ReadAsync_lowercases_host_patterns()
    {
        await CreatePackageAsync(CreateManifest(
            """{ "id": "x", "event": "httpRequestSending", "command": "node", "matcher": { "hosts": ["*.API.Example.com"] } }""",
            AllHookPermissions));

        var hooks = (await CreateReader().ReadAsync(Path.Combine(_rootPath, "plugin.json"))).Contributions.Hooks;

        hooks.Single().Matcher.HostPatterns.Should().Equal("*.api.example.com");
    }

    [Theory]
    [InlineData("\"node\"", true)]
    [InlineData("\"C:/tools/guard.exe\"", true)]
    [InlineData("\"C:\\\\tools\\\\guard.exe\"", true)]
    [InlineData("\"hooks/guard.exe\"", false)]
    [InlineData("\"./guard.exe\"", false)]
    [InlineData("\"${workspaceRoot}/guard.exe\"", false)]
    public async Task ReadAsync_accepts_only_path_unambiguous_commands(string commandJson, bool valid)
    {
        await CreatePackageAsync(CreateManifest(
            $$"""{ "id": "x", "event": "toolExecuting", "command": {{commandJson}} }""",
            AllHookPermissions));

        var action = () => CreateReader().ReadAsync(Path.Combine(_rootPath, "plugin.json"));

        if (valid)
        {
            await action.Should().NotThrowAsync();
        }
        else
        {
            await action.Should().ThrowAsync<InvalidDataException>().WithMessage("*must be a bare executable name*");
        }
    }

    [Fact]
    public async Task ReadAsync_accepts_origins_on_tool_and_http_events()
    {
        await CreatePackageAsync(CreateManifest(
            """
            { "id": "tool", "event": "toolExecuting", "command": "node", "matcher": { "origins": ["subagent"] } },
            { "id": "http", "event": "httpRequestSending", "command": "node", "matcher": { "origins": ["continuation"] } }
            """,
            AllHookPermissions));

        var hooks = (await CreateReader().ReadAsync(Path.Combine(_rootPath, "plugin.json"))).Contributions.Hooks;

        hooks[0].Matcher.Origins.Should().Equal(DirectTurnOrigin.Subagent);
        hooks[1].Matcher.Origins.Should().Equal(DirectTurnOrigin.Continuation);
    }

    [Fact]
    public async Task ReadAsync_accepts_source_ids_and_kinds_on_tool_events()
    {
        await CreatePackageAsync(CreateManifest(
            """{ "id": "x", "event": "toolExecuting", "command": "node", "matcher": { "sourceIds": ["github/*"], "kinds": ["run"], "sources": ["builtIn"] } }""",
            AllHookPermissions));

        var hook = (await CreateReader().ReadAsync(Path.Combine(_rootPath, "plugin.json"))).Contributions.Hooks.Single();

        hook.Matcher.SourceIdPatterns.Should().Equal("github/*");
        hook.Matcher.Kinds.Should().Equal(ToolCallKind.Run);
        hook.Matcher.Sources.Should().Equal(ToolSourceKind.BuiltIn);
    }

    [Fact]
    public async Task ReadAsync_allows_an_empty_hook_array()
    {
        await CreatePackageAsync(CreateManifest(string.Empty, AllHookPermissions));

        var manifest = await CreateReader().ReadAsync(Path.Combine(_rootPath, "plugin.json"));

        manifest.Contributions.Hooks.Should().BeEmpty();
    }

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
        {
            Directory.Delete(_rootPath, true);
        }
    }

    private async Task CreatePackageAsync(string manifest)
    {
        Directory.CreateDirectory(Path.Combine(_rootPath, "hooks"));
        await File.WriteAllTextAsync(Path.Combine(_rootPath, "plugin.json"), manifest);
        await File.WriteAllTextAsync(Path.Combine(_rootPath, "hooks", "guard.ps1"), "# guard");
        await File.WriteAllTextAsync(Path.Combine(_rootPath, "hooks", "audit.js"), "// audit");
    }

    private static string CreateManifest(string hooksJson, string permissions)
        => $$"""
            {
              "schemaVersion": 1,
              "id": "shell-guard",
              "name": "Shell Guard",
              "version": "1.0.0",
              "permissions": [{{permissions}}],
              "contributes": { "hooks": [{{hooksJson}}] }
            }
            """;

    private static PluginManifestReader CreateReader()
        => new(new ExtensionPackageLimits(1024 * 1024, 4 * 1024 * 1024, 100, 1024 * 1024, 256 * 1024));

    private const string ValidHookManifest = """
        {
          "schemaVersion": 1,
          "id": "shell-guard",
          "name": "Shell Guard",
          "version": "1.0.0",
          "permissions": ["hooks.run", "hooks.tool"],
          "contributes": {
            "hooks": [
              {
                "id": "deny-dangerous-shell",
                "event": "toolExecuting",
                "matcher": { "tools": ["run_shell_command", "mcp__github__*"] },
                "command": "powershell.exe",
                "arguments": ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
                              "-File", "${pluginRoot}/hooks/guard.ps1"],
                "timeoutSeconds": 10,
                "onFailure": "block"
              },
              {
                "id": "audit-run",
                "event": "runCompleted",
                "matcher": { "origins": ["interactive", "subagent"] },
                "command": "node",
                "arguments": ["${pluginRoot}/hooks/audit.js"]
              }
            ]
          }
        }
        """;
}
