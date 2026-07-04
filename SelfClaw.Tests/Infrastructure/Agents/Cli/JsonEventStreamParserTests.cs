using FluentAssertions;
using SelfClaw.Core.Runtime.Agent;
using SelfClaw.Infrastructure.Agents.Cli.Parsers;

namespace SelfClaw.Tests.Infrastructure.Agents.Cli;

public sealed class JsonEventStreamParserTests
{
    // Captured from OpenCode 1.17.13 `run --format json`: tool calls arrive with a top-level
    // type of "tool_use" while the nested part keeps `type: "tool"`.
    private const string OpenCodeToolUseLine =
        """
        {"type":"tool_use","timestamp":1783136962279,"sessionID":"ses_1","part":{"type":"tool","tool":"read","callID":"call_1","state":{"status":"completed","input":{"filePath":"sample.txt"},"output":"file content"},"id":"prt_1","sessionID":"ses_1","messageID":"msg_1"}}
        """;

    [Fact]
    public void OpenCode_tool_use_event_emits_started_and_completed()
    {
        var parser = new JsonEventStreamParser(CliAgentKind.OpenCode);

        var events = parser.Feed(OpenCodeToolUseLine + "\n").ToArray();

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
    public void OpenCode_legacy_tool_event_type_is_still_recognised()
    {
        var parser = new JsonEventStreamParser(CliAgentKind.OpenCode);
        var line =
            """
            {"type":"tool","part":{"type":"tool","tool":"bash","callID":"call_2","state":{"status":"completed","input":{"command":"ls"},"output":"ok"}}}
            """;

        var events = parser.Feed(line + "\n").ToArray();

        events.Should().HaveCount(2);
        events[0].Should().BeOfType<ToolCallStartedEvent>()
            .Which.ToolName.Should().Be("bash");
        events[1].Should().BeOfType<ToolCallCompletedEvent>()
            .Which.Status.Should().Be(ToolCallStatus.Completed);
    }

    [Fact]
    public void OpenCode_running_tool_use_emits_started_once_then_completed()
    {
        var parser = new JsonEventStreamParser(CliAgentKind.OpenCode);
        var runningLine =
            """
            {"type":"tool_use","part":{"type":"tool","tool":"read","callID":"call_3","state":{"status":"running","input":{"filePath":"a.txt"}}}}
            """;
        var completedLine =
            """
            {"type":"tool_use","part":{"type":"tool","tool":"read","callID":"call_3","state":{"status":"completed","input":{"filePath":"a.txt"},"output":"done"}}}
            """;

        var first = parser.Feed(runningLine + "\n").ToArray();
        var second = parser.Feed(completedLine + "\n").ToArray();

        first.Should().ContainSingle().Which.Should().BeOfType<ToolCallStartedEvent>();
        second.Should().ContainSingle().Which.Should().BeOfType<ToolCallCompletedEvent>();
    }
}
