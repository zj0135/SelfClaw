using SelfClaw.Infrastructure.Agents.Direct.Tools;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.AI;
using SelfClaw.Core.Runtime.Agent;
using SelfClaw.Infrastructure.Extensions.Mcp;

namespace SelfClaw.Tests.Infrastructure.Agents.Direct.Tools;

public sealed class McpToolAdapterTests
{
    [Fact]
    public void CreateProviderName_UsesNamespaceAllowedCharactersAndStableHash()
    {
        var name = McpToolAdapter.CreateProviderName(
            "Git Server!",
            new string('x', 100) + " status?");

        name.Should().StartWith("mcp__git_server___");
        name.Should().HaveLength(McpToolAdapter.MaximumProviderNameLength);
        name.Should().MatchRegex("^[a-z0-9_-]+$");
        McpToolAdapter.CreateProviderName("Git Server!", new string('x', 100) + " status?")
            .Should().Be(name);
    }

    [Fact]
    public void DescribeResult_MapsTextAndStructuredContent()
    {
        var element = JsonSerializer.SerializeToElement(new
        {
            content = new[] { new { type = "text", text = "first line\nsecond line" } },
            structuredContent = new { count = 2 },
            isError = false
        });

        var result = McpToolResultFormatter.DescribeResult(element);

        result.Status.Should().Be(ToolCallStatus.Completed);
        result.Summary.Should().Be("first line");
        result.Detail.Should().Contain("second line").And.Contain("\"count\": 2");
    }

    [Fact]
    public void DescribeResult_MapsNonTextContentWithoutBase64()
    {
        var element = JsonSerializer.SerializeToElement(new
        {
            content = new[] { new { type = "image", mimeType = "image/png", data = "YWJjZA==" } },
            isError = false
        });

        var result = McpToolResultFormatter.DescribeResult(element);

        result.Summary.Should().Be("1 image");
        result.Detail.Should().Be("[image: image/png, 6 bytes]");
        result.Detail.Should().NotContain("YWJjZA");
    }

    [Fact]
    public void DescribeResult_IsErrorMapsFailedWithoutThrowing()
    {
        var element = JsonSerializer.SerializeToElement(new
        {
            content = new[] { new { type = "text", text = "server rejected input" } },
            isError = true
        });

        var result = McpToolResultFormatter.DescribeResult(element);

        result.Status.Should().Be(ToolCallStatus.Failed);
        result.Summary.Should().Be("server rejected input");
    }

    [Fact]
    public void LimitModelResult_TruncatesLargeJson()
    {
        var element = JsonSerializer.SerializeToElement(new { text = new string('x', 70 * 1024), isError = true });

        var result = McpToolResultFormatter.Format(element);

        var truncated = result.Content;
        JsonSerializer.SerializeToUtf8Bytes(result).Length.Should().BeLessThanOrEqualTo(McpToolResultFormatter.MaximumModelResultBytes);
        truncated.GetProperty("isError").GetBoolean().Should().BeTrue();
        McpToolResultFormatter.DescribeResult(truncated).Status.Should().Be(ToolCallStatus.Failed);
    }

    [Fact]
    public void DescribeResult_maps_real_sdk_text_content()
    {
        var result = McpToolResultFormatter.DescribeResult(new TextContent("first line\nsecond line"));

        result.Status.Should().Be(ToolCallStatus.Completed);
        result.Summary.Should().Be("first line");
        result.Detail.Should().Contain("second line");
    }

    [Fact]
    public void LimitModelResult_truncates_real_sdk_text_content()
    {
        var result = McpToolResultFormatter.Format(new TextContent(new string('x', 70 * 1024)));

        JsonSerializer.SerializeToUtf8Bytes(result).Length.Should().BeLessThanOrEqualTo(McpToolResultFormatter.MaximumModelResultBytes);
        result.Detail.Should().EndWith("[SelfClaw truncated the MCP tool result at 64 KiB.]");
    }

    [Theory]
    [InlineData("ascii")]
    [InlineData("中文")]
    [InlineData("🙂")]
    [InlineData("\"")]
    [InlineData("\\")]
    public void Final_utf8_budget_includes_envelope_escaping_and_error_semantics(string unit)
    {
        var text = string.Concat(Enumerable.Repeat(unit, 70_000));
        var input = JsonSerializer.SerializeToElement(new { content = new[] { new { type = "text", text } }, isError = true });
        var result = McpToolResultFormatter.Format(input);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(result);

        bytes.Length.Should().BeLessThanOrEqualTo(McpToolResultFormatter.MaximumModelResultBytes);
        using var parsed = JsonDocument.Parse(bytes);
        parsed.RootElement.GetProperty("Content").GetProperty("truncated").GetBoolean().Should().BeTrue();
        result.Status.Should().Be(ToolCallStatus.Failed);
        result.Content.GetProperty("isError").GetBoolean().Should().BeTrue();
        result.Detail.Should().Contain("truncated").And.NotContain("\uFFFD");
    }

    [Fact]
    public void Multiple_sdk_content_blocks_share_one_final_budget()
    {
        AIContent[] input = [new TextContent(new string('a', 50_000)), new TextContent(new string('b', 50_000))];
        var result = McpToolResultFormatter.Format(input);

        JsonSerializer.SerializeToUtf8Bytes(result).Length.Should().BeLessThanOrEqualTo(McpToolResultFormatter.MaximumModelResultBytes);
        result.Content.GetProperty("truncated").GetBoolean().Should().BeTrue();
    }
}
