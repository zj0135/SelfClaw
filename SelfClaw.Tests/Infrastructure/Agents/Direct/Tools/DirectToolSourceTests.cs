using System.Text.Json;
using FluentAssertions;
using SelfClaw.Core.Runtime.Agent;
using SelfClaw.Infrastructure.Agents.Direct.Tools;
using SelfClaw.Infrastructure.Agents.Direct.Tools.Models;

namespace SelfClaw.Tests.Infrastructure.Agents.Direct.Tools;

public sealed class DirectToolSourceTests
{
    [Fact]
    public void ResolveId_prefers_an_explicit_descriptor_source()
    {
        var descriptor = new DirectToolDescriptor(ToolCallKind.Read, ToolSourceKind.Mcp, "server");

        DirectToolSource.ResolveId(descriptor, null).Should().Be("server");
    }

    [Fact]
    public void ResolveId_reads_the_skill_id_from_the_call_arguments()
    {
        var descriptor = new DirectToolDescriptor(ToolCallKind.Run, ToolSourceKind.Skill);

        DirectToolSource.ResolveId(descriptor, new Dictionary<string, object?> { ["skillId"] = "office/review" })
            .Should().Be("office/review");
    }

    [Fact]
    public void ResolveId_reads_the_skill_id_from_provider_shaped_arguments()
    {
        // MEAI's OpenAI connector deserializes a tool call's arguments into Dictionary<string, object?>,
        // whose values are JsonElement rather than string - exactly what DirectPromptComposer produces
        // when replaying a stored call. This is the shape every real call carries.
        var arguments = JsonSerializer.Deserialize<Dictionary<string, object?>>("""{"skillId":"office/review"}""");
        arguments.Should().NotBeNull();
        arguments.Should().ContainKey("skillId").WhoseValue.Should().BeOfType<JsonElement>();
        var descriptor = new DirectToolDescriptor(ToolCallKind.Run, ToolSourceKind.Skill);

        DirectToolSource.ResolveId(descriptor, arguments).Should().Be("office/review");
    }

    [Fact]
    public void ResolveId_returns_null_for_a_non_string_skill_argument()
    {
        var arguments = JsonSerializer.Deserialize<Dictionary<string, object?>>("""{"skillId":7}""");
        var descriptor = new DirectToolDescriptor(ToolCallKind.Run, ToolSourceKind.Skill);

        DirectToolSource.ResolveId(descriptor, arguments).Should().BeNull();
    }

    [Fact]
    public void ResolveId_returns_null_without_a_skill_argument()
    {
        var descriptor = new DirectToolDescriptor(ToolCallKind.Run, ToolSourceKind.Skill);

        DirectToolSource.ResolveId(descriptor, new Dictionary<string, object?>()).Should().BeNull();
        DirectToolSource.ResolveId(descriptor, null).Should().BeNull();
    }

    [Fact]
    public void ResolveId_ignores_a_skill_argument_for_a_non_skill_source()
    {
        var descriptor = new DirectToolDescriptor(ToolCallKind.Run, ToolSourceKind.BuiltIn);

        DirectToolSource.ResolveId(descriptor, new Dictionary<string, object?> { ["skillId"] = "review" })
            .Should().BeNull();
    }
}
