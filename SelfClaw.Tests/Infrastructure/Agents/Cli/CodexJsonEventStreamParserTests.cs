using FluentAssertions;
using SelfClaw.Core.Runtime.Agent;
using SelfClaw.Infrastructure.Agents.Cli.Parsers;

namespace SelfClaw.Tests.Infrastructure.Agents.Cli;

public sealed class CodexJsonEventStreamParserTests
{
    [Fact]
    public void Thread_started_emits_run_started_once()
    {
        var parser = new CodexJsonEventStreamParser();
        const string line = """{"type":"thread.started","thread_id":"thread-1"}""";

        var first = parser.ParseLine(line).ToArray();
        var second = parser.ParseLine(line).ToArray();

        first.Should().HaveCount(2);
        var started = first[0].Should().BeOfType<RunStartedEvent>().Subject;
        started.SessionId.Should().Be("thread-1");
        started.AgentKind.Should().Be(CliAgentKind.Codex);
        second.Should().BeEmpty();
    }

    [Fact]
    public void Completed_message_and_turn_usage_are_mapped()
    {
        var parser = new CodexJsonEventStreamParser();
        const string messageLine =
            """
            {"type":"item.completed","item":{"id":"message-1","type":"agent_message","text":"answer"}}
            """;
        const string usageLine =
            """
            {"type":"turn.completed","usage":{"input_tokens":12,"output_tokens":8}}
            """;

        var events = parser.ParseLine(messageLine)
            .Concat(parser.ParseLine(usageLine))
            .ToArray();

        events.OfType<AssistantTextDeltaEvent>().Should().ContainSingle()
            .Which.Delta.Should().Be("answer");
        events.OfType<UsageReportedEvent>().Should().ContainSingle()
            .Which.Should().Be(new UsageReportedEvent(12, 8));
    }

    [Fact]
    public void Completed_tool_without_start_synthesizes_matching_pair()
    {
        var parser = new CodexJsonEventStreamParser();
        const string line =
            """
            {"type":"item.completed","item":{"id":"tool-1","type":"command_execution","command":"dotnet test","status":"completed","aggregated_output":"passed"}}
            """;

        var events = parser.ParseLine(line).ToArray();

        events.Should().HaveCount(2);
        events[0].Should().BeOfType<ToolCallStartedEvent>()
            .Which.Kind.Should().Be(ToolCallKind.Run);
        events[1].Should().BeOfType<ToolCallCompletedEvent>()
            .Which.ResultContent.Should().Be("passed");
    }

    [Fact]
    public void Error_event_emits_failed_completion()
    {
        var parser = new CodexJsonEventStreamParser();

        var events = parser.ParseLine("""{"type":"error","message":"boom"}""").ToArray();

        var completed = events.Should().ContainSingle()
            .Which.Should().BeOfType<RunCompletedEvent>().Subject;
        completed.Status.Should().Be(RunCompletionStatus.Failed);
        completed.ErrorMessage.Should().Be("boom");
    }
}
