using FluentAssertions;
using SelfClaw.Core.Runtime;
using SelfClaw.Core.Runtime.Agent;
using SelfClaw.Infrastructure.Agents.Direct.Hooks;
using SelfClaw.Infrastructure.Extensions.Plugins.Models;

namespace SelfClaw.Tests.Infrastructure.Agents.Direct.Hooks;

public sealed class HookMatcherTests
{
    [Theory]
    [InlineData("run_shell_command", "run_shell_command")]
    [InlineData("*", "anything")]
    [InlineData("*", "")]
    [InlineData("run_*", "run_shell_command")]
    [InlineData("*shell*", "run_shell_command")]
    [InlineData("mcp__*", "mcp__github__create_issue")]
    [InlineData("*command", "run_shell_command")]
    [InlineData("*__*__*", "mcp__github__create_issue")]
    public void MatchesWildcard_matches_star_sequences(string pattern, string value)
        => HookMatcher.MatchesWildcard(pattern, value, StringComparison.Ordinal).Should().BeTrue();

    [Theory]
    [InlineData("read_file", "write_file")]
    [InlineData("a*b", "ab_c")]
    [InlineData("", "value")]
    public void MatchesWildcard_rejects_non_matches(string pattern, string value)
        => HookMatcher.MatchesWildcard(pattern, value, StringComparison.Ordinal).Should().BeFalse();

    [Fact]
    public void MatchesWildcard_is_case_sensitive_for_ordinal()
        => HookMatcher.MatchesWildcard("RUN", "run", StringComparison.Ordinal).Should().BeFalse();

    [Theory]
    [InlineData("api.example.com", "api.example.com")]
    [InlineData("api.example.com", "API.EXAMPLE.COM")]
    [InlineData("*.example.com", "api.example.com")]
    [InlineData("*.example.com", "a.b.example.com")]
    [InlineData("127.0.0.1", "127.0.0.1")]
    [InlineData("::1", "[::1]")]
    public void MatchesHost_matches_hosts_and_literals(string pattern, string host)
        => HookMatcher.MatchesHost(pattern, host).Should().BeTrue();

    [Theory]
    [InlineData("*.example.com", "example.com")]
    [InlineData("api.example.com", "other.example.com")]
    [InlineData("example.com", "*.example.com")]
    [InlineData("127.0.0.1", "127.0.0.2")]
    public void MatchesHost_rejects_non_matches(string pattern, string host)
        => HookMatcher.MatchesHost(pattern, host).Should().BeFalse();

    [Fact]
    public void MatchesTurn_uses_origins_as_an_or_filter()
    {
        var matcher = new PluginHookMatcher([DirectTurnOrigin.Subagent], [], [], [], [], []);
        HookMatcher.MatchesTurn(matcher, DirectTurnOrigin.Subagent).Should().BeTrue();
        HookMatcher.MatchesTurn(matcher, DirectTurnOrigin.Interactive).Should().BeFalse();
        HookMatcher.MatchesTurn(HookTestFactoryEmptyMatcher(), DirectTurnOrigin.Interactive).Should().BeTrue();
    }

    [Fact]
    public void MatchesTool_ands_fields_and_ors_values()
    {
        var matcher = new PluginHookMatcher(
            [],
            ["run_*", "write_file"],
            ["github/*"],
            [ToolCallKind.Run],
            [ToolSourceKind.Mcp],
            []);

        HookMatcher.MatchesTool(
            matcher, DirectTurnOrigin.Interactive, "run_shell_command", "github/main", ToolCallKind.Run, ToolSourceKind.Mcp)
            .Should().BeTrue();
        HookMatcher.MatchesTool(
            matcher, DirectTurnOrigin.Interactive, "run_shell_command", "github/main", ToolCallKind.Read, ToolSourceKind.Mcp)
            .Should().BeFalse();
        HookMatcher.MatchesTool(
            matcher, DirectTurnOrigin.Interactive, "run_shell_command", "other/main", ToolCallKind.Run, ToolSourceKind.Mcp)
            .Should().BeFalse();
        HookMatcher.MatchesTool(
            matcher, DirectTurnOrigin.Interactive, "read_file", "github/main", ToolCallKind.Run, ToolSourceKind.Mcp)
            .Should().BeFalse();
    }

    [Fact]
    public void MatchesTool_never_matches_an_absent_source_id()
    {
        var matcher = new PluginHookMatcher([], [], ["github/*"], [], [], []);
        HookMatcher.MatchesTool(
            matcher, DirectTurnOrigin.Interactive, "read_file", null, ToolCallKind.Read, ToolSourceKind.BuiltIn)
            .Should().BeFalse();
    }

    [Fact]
    public void MatchesTool_treats_source_ids_case_insensitively()
    {
        var matcher = new PluginHookMatcher([], [], ["GitHub/*"], [], [], []);
        HookMatcher.MatchesTool(
            matcher, DirectTurnOrigin.Interactive, "tool", "github/Issue", ToolCallKind.Other, ToolSourceKind.Mcp)
            .Should().BeTrue();
    }

    [Fact]
    public void MatchesHttp_uses_host_patterns()
    {
        var matcher = new PluginHookMatcher([], [], [], [], [], ["*.openai.azure.com"]);
        HookMatcher.MatchesHttp(matcher, DirectTurnOrigin.Interactive, "tenant.openai.azure.com").Should().BeTrue();
        HookMatcher.MatchesHttp(matcher, DirectTurnOrigin.Interactive, "openai.azure.com").Should().BeFalse();
    }

    private static PluginHookMatcher HookTestFactoryEmptyMatcher() => new([], [], [], [], [], []);
}
