using FluentAssertions;
using SelfClaw.Core.Runtime.Agent;
using SelfClaw.Infrastructure.Agents.Cli.Parsers;

namespace SelfClaw.Tests.Infrastructure.Agents.Cli;

public sealed class OpenCodeJsonEventStreamParserTests
{
    private const string ToolUseLine =
        """
        {"type":"tool_use","timestamp":1783136962279,"sessionID":"ses_1","part":{"type":"tool","tool":"read","callID":"call_1","state":{"status":"completed","input":{"filePath":"sample.txt"},"output":"file content"},"id":"prt_1","sessionID":"ses_1","messageID":"msg_1"}}
        """;

    [Fact]
    public void Tool_use_event_emits_started_and_completed()
    {
        var parser = new OpenCodeJsonEventStreamParser();

        var events = parser.ParseLine(ToolUseLine).ToArray();

        events.Should().HaveCount(2);
        var started = events[0].Should().BeOfType<ToolCallStartedEvent>().Subject;
        started.ToolCallId.Should().Be("call_1");
        started.ToolName.Should().Be("read");
        started.ArgumentsJson.Should().Contain("sample.txt");

        var completed = events[1].Should().BeOfType<ToolCallCompletedEvent>().Subject;
        completed.ToolCallId.Should().Be("call_1");
        completed.Status.Should().Be(ToolCallStatus.Completed);
        completed.ResultContent.Should().Be("file content");
    }

    [Fact]
    public void Legacy_tool_event_type_is_still_recognized()
    {
        var parser = new OpenCodeJsonEventStreamParser();
        const string line =
            """
            {"type":"tool","part":{"type":"tool","tool":"bash","callID":"call_2","state":{"status":"completed","input":{"command":"ls"},"output":"ok"}}}
            """;

        var events = parser.ParseLine(line).ToArray();

        events.Should().HaveCount(2);
        events[0].Should().BeOfType<ToolCallStartedEvent>()
            .Which.ToolName.Should().Be("bash");
        events[1].Should().BeOfType<ToolCallCompletedEvent>()
            .Which.Status.Should().Be(ToolCallStatus.Completed);
    }

    [Fact]
    public void Running_tool_use_emits_started_once_then_completed()
    {
        var parser = new OpenCodeJsonEventStreamParser();
        const string runningLine =
            """
            {"type":"tool_use","part":{"type":"tool","tool":"read","callID":"call_3","state":{"status":"running","input":{"filePath":"a.txt"}}}}
            """;
        const string completedLine =
            """
            {"type":"tool_use","part":{"type":"tool","tool":"read","callID":"call_3","state":{"status":"completed","input":{"filePath":"a.txt"},"output":"done"}}}
            """;

        var first = parser.ParseLine(runningLine).ToArray();
        var second = parser.ParseLine(completedLine).ToArray();

        first.Should().ContainSingle().Which.Should().BeOfType<ToolCallStartedEvent>();
        second.Should().ContainSingle().Which.Should().BeOfType<ToolCallCompletedEvent>();
    }
}
