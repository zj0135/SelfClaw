using System.Text;
using System.Text.Json;
using FluentAssertions;
using SelfClaw.Infrastructure.Agents.Direct.Hooks;
using SelfClaw.Infrastructure.Agents.Direct.Hooks.Models;

namespace SelfClaw.Tests.Infrastructure.Agents.Direct.Hooks;

public sealed class HookProtocolTests
{
    [Fact]
    public void Truncate_keeps_a_multi_byte_character_intact()
    {
        // '€' is three UTF-8 bytes: a limit of four bytes can only keep the first character.
        var (text, truncated) = HookProtocol.Truncate("€€", 4);

        text.Should().Be("€");
        truncated.Should().BeTrue();
        Encoding.UTF8.GetByteCount(text).Should().Be(3);
    }

    [Fact]
    public void Truncate_returns_the_input_when_it_fits()
    {
        var (text, truncated) = HookProtocol.Truncate("value", 5);
        text.Should().Be("value");
        truncated.Should().BeFalse();
    }

    [Fact]
    public void Truncate_never_splits_a_surrogate_pair()
    {
        var (text, truncated) = HookProtocol.Truncate("😀😀", 5);

        text.Should().Be("😀");
        truncated.Should().BeTrue();
    }

    [Fact]
    public void Parse_returns_no_decision_for_empty_output()
    {
        var result = HookProtocol.Parse<RunStartingDecision>("  \n", ["continue", "block"]);

        result.Decision.Should().BeNull();
        result.FailureKind.Should().BeNull();
    }

    [Fact]
    public void Parse_accepts_a_leading_bom()
    {
        var json = "\uFEFF" + """{"decision":"block","reason":"no"}""";

        var result = HookProtocol.Parse<RunStartingDecision>(json, ["continue", "block"]);

        result.Decision!.Decision.Should().Be("block");
        result.FailureKind.Should().BeNull();
    }

    [Fact]
    public void Parse_reports_invalid_output_for_a_non_object_document()
    {
        HookProtocol.Parse<RunStartingDecision>("[]", ["continue"]).FailureKind.Should().Be("invalidOutput");
        HookProtocol.Parse<RunStartingDecision>("\"text\"", ["continue"]).FailureKind.Should().Be("invalidOutput");
        HookProtocol.Parse<RunStartingDecision>("{not json", ["continue"]).FailureKind.Should().Be("invalidOutput");
    }

    [Fact]
    public void Parse_reports_invalid_decision_for_a_wrong_type_or_value()
    {
        HookProtocol.Parse<RunStartingDecision>("""{"decision":1}""", ["continue", "block"])
            .FailureKind.Should().Be("invalidDecision");
        HookProtocol.Parse<RunStartingDecision>("""{"decision":"deny"}""", ["continue", "block"])
            .FailureKind.Should().Be("invalidDecision");
        HookProtocol.Parse<ToolExecutingDecision>("""{"reason":1}""", ["continue"])
            .FailureKind.Should().Be("invalidDecision");
    }

    [Fact]
    public void Parse_ignores_unknown_properties()
    {
        var result = HookProtocol.Parse<ToolExecutedDecision>("""{"feedback":"ok","decision":"deny"}""", []);

        result.Decision!.Feedback.Should().Be("ok");
        result.FailureKind.Should().BeNull();
    }

    [Fact]
    public void Parse_requires_exact_casing()
    {
        HookProtocol.Parse<RunStartingDecision>("""{"Decision":"block"}""", ["continue", "block"])
            .Decision!.Decision.Should().BeNull();
    }

    [Fact]
    public void Serialize_uses_camel_case_names_and_enum_strings()
    {
        var bytes = HookProtocol.Serialize(new HookUsagePayload(1, 2));

        Encoding.UTF8.GetString(bytes).Should().Be("""{"inputTokens":1,"outputTokens":2}""");
        bytes[0].Should().NotBe(0xEF);
    }

    [Fact]
    public void TruncateJson_replaces_an_oversized_value_with_text()
    {
        var oversized = JsonSerializer.SerializeToElement(new string('x', HookProtocol.MaximumJsonValueBytes + 1));

        var (value, truncated) = HookProtocol.TruncateJson(oversized);

        truncated.Should().BeTrue();
        value.ValueKind.Should().Be(JsonValueKind.String);
    }

    [Fact]
    public void TruncateJson_keeps_a_small_value_verbatim()
    {
        var value = JsonSerializer.SerializeToElement(new { a = 1 });

        var (result, truncated) = HookProtocol.TruncateJson(value);

        truncated.Should().BeFalse();
        result.ValueKind.Should().Be(JsonValueKind.Object);
    }
}
