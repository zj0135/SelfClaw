using System.Net.Http.Headers;
using FluentAssertions;
using SelfClaw.Infrastructure.Agents.Direct.Hooks;

namespace SelfClaw.Tests.Infrastructure.Agents.Direct.Hooks;

public sealed class HttpHeaderRedactorTests
{
    [Fact]
    public void Redact_keeps_whitelisted_values_and_hides_everything_else()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "https://example.com")
        {
            Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json")
        };
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer secret");
        request.Headers.TryAddWithoutValidation("X-Request-Id", "abc");

        var result = HttpHeaderRedactor.Redact(request.Headers, request.Content.Headers);

        result["authorization"].Should().Be("[redacted]");
        result["content-type"].Should().Be("application/json; charset=utf-8");
        result["x-request-id"].Should().Be("abc");
    }

    [Fact]
    public void Redact_keeps_rate_limit_prefixes_and_joins_multiple_values()
    {
        var headers = new HttpResponseMessage().Headers;
        headers.TryAddWithoutValidation("x-ratelimit-remaining-requests", ["10", "9"]);
        headers.TryAddWithoutValidation("anthropic-ratelimit-tokens", "100");

        var result = HttpHeaderRedactor.Redact(headers, null);

        result["x-ratelimit-remaining-requests"].Should().Be("10, 9");
        result["anthropic-ratelimit-tokens"].Should().Be("100");
    }

    [Fact]
    public void Redact_merges_content_headers()
    {
        var response = new HttpResponseMessage
        {
            Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json")
        };

        var result = HttpHeaderRedactor.Redact(response.Headers, response.Content.Headers);

        result["content-type"].Should().Be("application/json; charset=utf-8");
    }
}
