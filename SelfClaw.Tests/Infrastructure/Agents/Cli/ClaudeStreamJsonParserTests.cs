using FluentAssertions;
using SelfClaw.Core.Runtime.Agent;
using SelfClaw.Infrastructure.Agents.Cli.Parsers;

namespace SelfClaw.Tests.Infrastructure.Agents.Cli;

public sealed class ClaudeStreamJsonParserTests
{
    [Fact]
    public void System_init_emits_run_started_once()
    {
        var parser = new ClaudeStreamJsonParser();
        var line =
            """
            {"type":"system","subtype":"init","session_id":"sess_1","model":"claude-sonnet-4"}
            """;

        var first = parser.ParseLine(line).ToArray();
        var second = parser.ParseLine(line).ToArray();

        first.Should().HaveCount(2);
        var started = first[0].Should().BeOfType<RunStartedEvent>().Subject;
        started.SessionId.Should().Be("sess_1");
        started.Model.Should().Be("claude-sonnet-4");
        started.AgentKind.Should().Be(CliAgentKind.Claude);
        first[1].Should().BeOfType<RunStatusEvent>().Which.Status.Should().Be(AgentRunStatus.Initializing);

        // The run only starts once even if a second init line arrives.
        second.Should().BeEmpty();
    }

    [Fact]
    public void Partial_text_delta_streams_and_full_message_is_deduped()
    {
        var parser = new ClaudeStreamJsonParser();
        var messageStart =
            """
            {"type":"stream_event","event":{"type":"message_start","message":{"id":"msg_1"}}}
            """;
        var textDelta =
            """
            {"type":"stream_event","event":{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Hello"}}}
            """;
        // The full assistant message repeats the block that already streamed as a delta.
        var fullMessage =
            """
            {"type":"assistant","message":{"id":"msg_1","content":[{"type":"text","text":"Hello"}]}}
            """;

        var events = parser.ParseLine(messageStart).ToArray()
            .Concat(parser.ParseLine(textDelta).ToArray())
            .Concat(parser.ParseLine(fullMessage).ToArray())
            .ToArray();

        events.OfType<AssistantTextDeltaEvent>().Should().ContainSingle()
            .Which.Should().Be(new AssistantTextDeltaEvent("msg_1:0", "Hello"));
    }

    [Fact]
    public void Full_message_without_partials_emits_text_and_tool_use()
    {
        var parser = new ClaudeStreamJsonParser();
        var line =
            """
            {"type":"assistant","message":{"id":"msg_2","content":[{"type":"text","text":"Reading"},{"type":"tool_use","id":"tool_1","name":"Read","input":{"file_path":"a.cs"}}]}}
            """;

        var events = parser.ParseLine(line).ToArray();

        events.OfType<AssistantTextDeltaEvent>().Should().ContainSingle()
            .Which.Delta.Should().Be("Reading");
        var started = events.OfType<ToolCallStartedEvent>().Should().ContainSingle().Subject;
        started.ToolCallId.Should().Be("tool_1");
        started.ToolName.Should().Be("Read");
        started.Kind.Should().Be(ToolCallKind.Read);
        started.ArgumentsJson.Should().Contain("a.cs");
    }

    [Fact]
    public void User_tool_result_emits_completed()
    {
        var parser = new ClaudeStreamJsonParser();
        var line =
            """
            {"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"tool_1","content":"file body"}]}}
            """;

        var events = parser.ParseLine(line).ToArray();

        var completed = events.Should().ContainSingle().Which.Should().BeOfType<ToolCallCompletedEvent>().Subject;
        completed.ToolCallId.Should().Be("tool_1");
        completed.Status.Should().Be(ToolCallStatus.Completed);
        completed.ResultContent.Should().Be("file body");
    }

    [Fact]
    public void Result_emits_usage_then_completed_with_fallback_text()
    {
        var parser = new ClaudeStreamJsonParser();
        // Stream a text delta so the parser has accumulated text to fall back to.
        parser.ParseLine(
            """
            {"type":"stream_event","event":{"type":"message_start","message":{"id":"m"}}}
            """).ToArray();
        parser.ParseLine(
            """
            {"type":"stream_event","event":{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"answer"}}}
            """).ToArray();

        var resultLine =
            """
            {"type":"result","subtype":"success","is_error":false,"usage":{"input_tokens":11,"output_tokens":7}}
            """;
        var events = parser.ParseLine(resultLine).ToArray();

        events.OfType<UsageReportedEvent>().Should().ContainSingle()
            .Which.Should().Be(new UsageReportedEvent(11, 7));
        var completed = events.OfType<RunCompletedEvent>().Should().ContainSingle().Subject;
        completed.Status.Should().Be(RunCompletionStatus.Succeeded);
        completed.FinalText.Should().Be("answer");
    }

    [Fact]
    public void Invalid_json_line_becomes_raw_output()
    {
        var parser = new ClaudeStreamJsonParser();

        var events = parser.ParseLine("not json at all").ToArray();

        events.Should().ContainSingle().Which.Should().BeOfType<RawOutputEvent>()
            .Which.Line.Should().Be("not json at all");
    }

    [Fact]
    public void Blank_line_yields_nothing()
    {
        var parser = new ClaudeStreamJsonParser();

        parser.ParseLine("   ").Should().BeEmpty();
    }
}
