using SelfClaw.LlmProvider.Chat.Implementations;
using SelfClaw.LlmProvider.Client.Dtos;
using SelfClaw.LlmProvider.Client.Implementations;
using SelfClaw.LlmProvider.Parameters.Implementations;
using SelfClaw.LlmProvider.Profiles.Implementations;
using SelfClaw.LlmProvider.Protocols.Implementations;
using SelfClaw.LlmProvider.Quirks.Abstractions;
using SelfClaw.LlmProvider.Quirks.Dtos;
using SelfClaw.LlmProvider.Quirks.Implementations;
using SelfClaw.LlmProvider.Transport.Implementations;
using SelfClaw.LlmProvider.Vendors.Implementations;


namespace SelfClaw.Tests.LlmProvider;

/// <summary>
/// 一个调用方实现的 quirk：给每个请求加一个标记头。
/// 库本身不带任何针对特定网关的补偿，所以"某家网关需要额外处理"这件事在这里由测试自己表达。
/// </summary>
internal sealed class TestVendorQuirk : ILlmRequestQuirk
{
    internal const string QuirkId = "test-vendor-marker";
    internal const string HeaderName = "x-test-vendor";
    internal const string HeaderValue = "seen";

    internal static IReadOnlyDictionary<string, string> Parameters { get; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["value"] = HeaderValue };

    public string Id => QuirkId;

    public HttpMessageHandler Wrap(HttpMessageHandler inner, LlmQuirkContext context) =>
        new MarkerHandler(inner, context.Parameters.GetValueOrDefault("value") ?? HeaderValue);

    private sealed class MarkerHandler : DelegatingHandler
    {
        private readonly string _value;

        internal MarkerHandler(HttpMessageHandler innerHandler, string value)
        {
            InnerHandler = innerHandler;
            _value = value;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            request.Headers.TryAddWithoutValidation(HeaderName, _value);
            return base.SendAsync(request, cancellationToken);
        }
    }
}
