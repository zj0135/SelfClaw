using System.Text.Json;
using FluentAssertions;
using SelfClaw.Core.Models;
using SelfClaw.Core.Runtime;
using SelfClaw.Core.Runtime.Agent;
using SelfClaw.Infrastructure.Agents.Cli.Parsers;

namespace SelfClaw.Tests.Infrastructure.Agents.Cli;

public sealed class CliUsageNormalizationTests
{
    [Theory]
    [InlineData("codex")]
    [InlineData("claude")]
    [InlineData("opencode")]
    public void Complete_cli_observations_are_normalized_before_accumulation(string kind)
    {
        var first = Parse(kind, input: 3, output: 4);
        var second = Parse(kind, input: 5, output: 6);
        first.TotalTokens.Should().Be(7);
        second.TotalTokens.Should().Be(11);
        var accumulator = new TurnUsageAccumulator();
        accumulator.Observe(first);
        accumulator.Observe(second);
        accumulator.Build()!.TotalTokens.Should().Be(18);
        accumulator.Build()!.ProviderCalls.Should().Be(2);
    }

    [Theory]
    [InlineData("codex")]
    [InlineData("claude")]
    [InlineData("opencode")]
    public void Partial_cli_observations_cannot_fill_each_others_missing_totals(string kind)
    {
        var first = Parse(kind, input: 3, output: null);
        var second = Parse(kind, input: null, output: 4);
        first.TotalTokens.Should().BeNull();
        second.TotalTokens.Should().BeNull();
        var accumulator = new TurnUsageAccumulator();
        accumulator.Observe(first);
        accumulator.Observe(second);
        var result = accumulator.Build()!;
        result.InputTokens.Should().Be(3);
        result.OutputTokens.Should().Be(4);
        result.TotalTokens.Should().BeNull();
    }

    [Theory]
    [InlineData("codex", 7)]
    [InlineData("claude", 10)]
    [InlineData("opencode", 10)]
    public void Totals_use_inclusive_input_but_prefer_explicit_provider_totals(string kind, int derived)
    {
        Parse(kind, 3, 4, cache: true).TotalTokens.Should().Be(derived);
        Parse(kind, 3, 4, total: 41, cache: true).TotalTokens.Should().Be(41);
    }

    private static TurnUsage Parse(string kind, int? input, int? output, int? total = null, bool cache = false)
    {
        var usage = new Dictionary<string, object>();
        if (input is not null) usage[kind == "opencode" ? "input" : "input_tokens"] = input.Value;
        if (output is not null) usage[kind == "opencode" ? "output" : "output_tokens"] = output.Value;
        if (total is not null) usage[kind == "opencode" ? "total" : "total_tokens"] = total.Value;
        if (cache)
        {
            if (kind == "codex") usage["cached_input_tokens"] = 2;
            else if (kind == "opencode") usage["cache"] = new { read = 2, write = 1 };
            else
            {
                usage["cache_read_input_tokens"] = 2;
                usage["cache_creation_input_tokens"] = 1;
            }
        }

        var line = JsonSerializer.Serialize(new { type = kind switch
        {
            "codex" => "turn.completed",
            "claude" => "result",
            _ => "step_finish"
        }, usage });
        IEnumerable<AgentStreamEvent> events = kind switch
        {
            "codex" => new CodexJsonEventStreamParser().ParseLine(line),
            "claude" => new ClaudeStreamJsonParser().ParseLine(line),
            _ => new OpenCodeJsonEventStreamParser().ParseLine(line)
        };
        return events.OfType<UsageReportedEvent>().Should().ContainSingle().Subject.Usage;
    }
}
