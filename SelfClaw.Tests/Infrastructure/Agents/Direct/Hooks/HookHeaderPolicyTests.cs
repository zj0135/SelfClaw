using FluentAssertions;
using SelfClaw.Infrastructure.Agents.Direct.Hooks;

namespace SelfClaw.Tests.Infrastructure.Agents.Direct.Hooks;

public sealed class HookHeaderPolicyTests
{
    [Theory]
    [InlineData("traceparent")]
    [InlineData("tracestate")]
    [InlineData("baggage")]
    [InlineData("x-request-id")]
    [InlineData("X-Correlation-Id")]
    public void IsAllowed_accepts_trace_and_plain_x_headers(string name)
        => HookHeaderPolicy.IsAllowed(name, out _).Should().BeTrue();

    [Theory]
    [InlineData("authorization")]
    [InlineData("connection")]
    [InlineData("accept-encoding")]
    [InlineData("content-encoding")]
    [InlineData("te")]
    [InlineData("upgrade")]
    [InlineData("expect")]
    [InlineData("proxy-authorization")]
    [InlineData("if-none-match")]
    [InlineData("x-api-key")]
    [InlineData("x-session-id")]
    [InlineData("x-cookie")]
    [InlineData("x-signature")]
    [InlineData("x-password")]
    public void IsAllowed_rejects_protocol_and_sensitive_names(string name)
    {
        HookHeaderPolicy.IsAllowed(name, out var rejection).Should().BeFalse();
        rejection.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void IsAllowed_rejects_an_invalid_token_or_an_over_long_name()
    {
        HookHeaderPolicy.IsAllowed("x bad", out _).Should().BeFalse();
        HookHeaderPolicy.IsAllowed("x-" + new string('a', 64), out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("printable ASCII")]
    [InlineData("x")]
    public void IsValidValue_accepts_printable_ascii(string value)
        => HookHeaderPolicy.IsValidValue(value, out _).Should().BeTrue();

    [Theory]
    [InlineData("line\r\nbreak")]
    [InlineData("tab\tvalue")]
    [InlineData("中文")]
    public void IsValidValue_rejects_control_and_non_ascii(string value)
        => HookHeaderPolicy.IsValidValue(value, out _).Should().BeFalse();

    [Fact]
    public void IsValidValue_rejects_a_value_over_one_kib()
        => HookHeaderPolicy.IsValidValue(new string('a', 1025), out _).Should().BeFalse();
}
