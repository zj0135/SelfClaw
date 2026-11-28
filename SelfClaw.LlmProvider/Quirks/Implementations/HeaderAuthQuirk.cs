
using SelfClaw.LlmProvider.Errors;
using SelfClaw.LlmProvider.Quirks.Abstractions;
using SelfClaw.LlmProvider.Quirks.Dtos;

namespace SelfClaw.LlmProvider.Quirks.Implementations;

/// <summary>
/// 用描述符声明的认证方式覆盖 SDK 的默认放置。用于 Azure 这类要求 <c>api-key</c> 头而不是
/// <c>Authorization: Bearer</c> 的端点。
/// </summary>
internal sealed class HeaderAuthQuirk : ILlmRequestQuirk
{
    internal const string QuirkId = "header-auth";
    private const string HeaderNameParameter = "header";

    public string Id => QuirkId;

    public HttpMessageHandler Wrap(HttpMessageHandler inner, LlmQuirkContext context)
    {
        var name = context.Parameters.GetValueOrDefault(HeaderNameParameter)
            ?? context.Connection.Credential.Name
            ?? throw new LlmProviderException(
                $"The '{QuirkId}' quirk needs a header name, either as its 'header' parameter or on the credential.");
        return new HeaderAuthHandler(inner, name, context.Connection.Credential.Value);
    }

    private sealed class HeaderAuthHandler : DelegatingHandler
    {
        private readonly string _name;
        private readonly string _value;

        internal HeaderAuthHandler(HttpMessageHandler innerHandler, string name, string value)
        {
            InnerHandler = innerHandler;
            _name = name;
            _value = value;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (!request.Headers.Contains(_name))
            {
                request.Headers.TryAddWithoutValidation(_name, _value);
            }

            return base.SendAsync(request, cancellationToken);
        }
    }
}
