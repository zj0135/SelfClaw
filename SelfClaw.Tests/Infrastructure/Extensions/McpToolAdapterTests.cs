using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.AI;
using SelfClaw.Core.Runtime.Agent;
using SelfClaw.Infrastructure.Extensions.Mcp;

namespace SelfClaw.Tests.Infrastructure.Extensions;

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

        var result = McpToolAdapter.DescribeResult(element);

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

        var result = McpToolAdapter.DescribeResult(element);

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

        var result = McpToolAdapter.DescribeResult(element);

        result.Status.Should().Be(ToolCallStatus.Failed);
        result.Summary.Should().Be("server rejected input");
    }

    [Fact]
    public void LimitModelResult_TruncatesLargeJson()
    {
        var element = JsonSerializer.SerializeToElement(new { text = new string('x', 70 * 1024), isError = true });

        var result = McpToolAdapter.LimitModelResult(element);

        var truncated = result.Should().BeOfType<JsonElement>().Subject;
        truncated.GetRawText().Length.Should().BeLessThanOrEqualTo(McpToolAdapter.MaximumModelResultCharacters);
        truncated.GetProperty("isError").GetBoolean().Should().BeTrue();
        McpToolAdapter.DescribeResult(truncated).Status.Should().Be(ToolCallStatus.Failed);
    }

    [Fact]
    public void DescribeResult_maps_real_sdk_text_content()
    {
        var result = McpToolAdapter.DescribeResult(new TextContent("first line\nsecond line"));

        result.Status.Should().Be(ToolCallStatus.Completed);
        result.Summary.Should().Be("first line");
        result.Detail.Should().Contain("second line");
    }

    [Fact]
    public void LimitModelResult_truncates_real_sdk_text_content()
    {
        var result = McpToolAdapter.LimitModelResult(new TextContent(new string('x', 70 * 1024)));

        var text = result.Should().BeOfType<TextContent>().Which.Text;
        text.Length.Should().BeLessThanOrEqualTo(McpToolAdapter.MaximumModelResultCharacters);
        text.Should().EndWith("[SelfClaw truncated the MCP tool result at 64 KiB.]");
    }
}
