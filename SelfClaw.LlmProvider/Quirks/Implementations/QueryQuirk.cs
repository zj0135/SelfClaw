using SelfClaw.LlmProvider.Quirks.Abstractions;
using SelfClaw.LlmProvider.Quirks.Dtos;

namespace SelfClaw.LlmProvider.Quirks.Implementations;

/// <summary>把描述符里声明的静态查询参数加到每个请求上，例如 Azure 的 <c>api-version</c>。</summary>
internal sealed class QueryQuirk : ILlmRequestQuirk
{
    internal const string QuirkId = "query";

    public string Id => QuirkId;

    public HttpMessageHandler Wrap(HttpMessageHandler inner, LlmQuirkContext context)
    {
        if (context.Parameters.Count == 0)
        {
            return inner;
        }

        return new QueryHandler(inner, context.Parameters);
    }

    private sealed class QueryHandler : DelegatingHandler
    {
        private readonly IReadOnlyDictionary<string, string> _parameters;

        internal QueryHandler(HttpMessageHandler innerHandler, IReadOnlyDictionary<string, string> parameters)
        {
            InnerHandler = innerHandler;
            _parameters = parameters;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.RequestUri is { IsAbsoluteUri: true } uri)
            {
                var builder = new UriBuilder(uri);
                var existing = builder.Query.TrimStart('?');
                var appended = string.Join(
                    '&',
                    _parameters.Select(pair =>
                        $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(Resolve(pair.Value))}"));
                builder.Query = existing.Length == 0 ? appended : $"{existing}&{appended}";
                request.RequestUri = builder.Uri;
            }

            return base.SendAsync(request, cancellationToken);
        }

        private string Resolve(string value)
        {
            // 形如 {deployment} 的占位符从描述符参数里就地取值，避免为单个厂商再造一种机制。
            var resolved = value;
            foreach (var pair in _parameters)
            {
                resolved = resolved.Replace($"{{{pair.Key}}}", pair.Value, StringComparison.Ordinal);
            }

            return resolved;
        }
    }
}
