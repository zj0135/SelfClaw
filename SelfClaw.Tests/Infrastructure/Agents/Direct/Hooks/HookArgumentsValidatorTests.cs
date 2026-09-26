using System.Text.Json;
using FluentAssertions;
using SelfClaw.Infrastructure.Agents.Direct.Hooks;

namespace SelfClaw.Tests.Infrastructure.Agents.Direct.Hooks;

public sealed class HookArgumentsValidatorTests
{
    private static JsonElement Schema(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void TryValidate_accepts_a_matching_object()
    {
        var schema = Schema("""
            {"type":"object","properties":{"path":{"type":"string"},"count":{"type":"integer"}},
             "required":["path"],"additionalProperties":false}
            """);

        HookArgumentsValidator.TryValidate(Args("""{"path":"a.txt","count":2}"""), schema, out var error)
            .Should().BeTrue();
        error.Should().BeNull();
    }

    [Fact]
    public void TryValidate_rejects_a_non_object()
        => HookArgumentsValidator.TryValidate(Args("\"text\""), Schema("{}"), out _).Should().BeFalse();

    [Fact]
    public void TryValidate_rejects_a_missing_required_property()
    {
        var schema = Schema("""{"type":"object","properties":{"path":{"type":"string"}},"required":["path"]}""");

        HookArgumentsValidator.TryValidate(Args("{}"), schema, out var error).Should().BeFalse();
        error.Should().Contain("required");
    }

    [Theory]
    [InlineData("""{"value":"x"}""")]
    [InlineData("""{"value":1.5}""")]
    [InlineData("""{"value":null}""")]
    public void TryValidate_rejects_a_wrong_property_type(string arguments)
    {
        var schema = Schema("""{"type":"object","properties":{"value":{"type":"integer"}}}""");

        HookArgumentsValidator.TryValidate(Args(arguments), schema, out _).Should().BeFalse();
    }

    [Fact]
    public void TryValidate_accepts_an_undeclared_property_when_additional_properties_are_allowed()
    {
        var schema = Schema("""{"type":"object","properties":{"value":{"type":"string"}}}""");

        HookArgumentsValidator.TryValidate(Args("""{"value":"x","extra":1}"""), schema, out _).Should().BeTrue();
    }

    [Fact]
    public void TryValidate_rejects_an_undeclared_property_when_additional_properties_are_false()
    {
        var schema = Schema("""{"type":"object","properties":{"value":{"type":"string"}},"additionalProperties":false}""");

        HookArgumentsValidator.TryValidate(Args("""{"value":"x","extra":1}"""), schema, out var error).Should().BeFalse();
        error.Should().Contain("extra");
    }

    [Fact]
    public void TryValidate_rejects_an_oversized_object()
    {
        var schema = Schema("""{"type":"object"}""");
        var arguments = Args($$"""{"value":"{{new string('x', HookProtocol.MaximumJsonValueBytes + 1)}}"}""");

        HookArgumentsValidator.TryValidate(arguments, schema, out _).Should().BeFalse();
    }
}
