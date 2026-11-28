using System.Text.Json;


using SelfClaw.LlmProvider.Connections.Dtos;
using SelfClaw.LlmProvider.Errors;
using SelfClaw.LlmProvider.Quirks.Abstractions;
using SelfClaw.LlmProvider.Quirks.Dtos;

namespace SelfClaw.LlmProvider.Quirks.Implementations;

/// <summary>
/// 在请求路径前插入一段厂商要求的固定前缀，例如 Azure 的
/// <c>/openai/deployments/{deployment}</c>。占位符从连接选项就地取值。
/// </summary>
internal sealed class PathPrefixQuirk : ILlmRequestQuirk
{
    internal const string QuirkId = "path-prefix";
    private const string PrefixParameter = "prefix";

    public string Id => QuirkId;

    public HttpMessageHandler Wrap(HttpMessageHandler inner, LlmQuirkContext context)
    {
        var prefix = context.Parameters.GetValueOrDefault(PrefixParameter)
            ?? throw new LlmProviderException($"The '{QuirkId}' quirk requires a '{PrefixParameter}' parameter.");
        return new PathPrefixHandler(inner, Expand(prefix, context.Connection));
    }

    private static string Expand(string prefix, LlmConnection connection)
    {
        if (connection.Options is not { Count: > 0 } options)
        {
            return prefix;
        }

        var expanded = prefix;
        foreach (var pair in options)
        {
            var value = pair.Value.ValueKind == JsonValueKind.String
                ? pair.Value.GetString() ?? string.Empty
                : pair.Value.GetRawText();
            expanded = expanded.Replace($"{{{pair.Key}}}", value, StringComparison.Ordinal);
        }

        return expanded;
    }

    private sealed class PathPrefixHandler : DelegatingHandler
    {
        private readonly string _prefix;

        internal PathPrefixHandler(HttpMessageHandler innerHandler, string prefix)
        {
            InnerHandler = innerHandler;
            _prefix = prefix.TrimEnd('/');
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.RequestUri is { IsAbsoluteUri: true } uri && _prefix.Length > 0)
            {
                var builder = new UriBuilder(uri) { Path = $"{_prefix}{uri.AbsolutePath}" };
                request.RequestUri = builder.Uri;
            }

            return base.SendAsync(request, cancellationToken);
        }
    }
}
