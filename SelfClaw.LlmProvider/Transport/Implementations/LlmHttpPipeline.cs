
using SelfClaw.LlmProvider.Connections.Dtos;
using SelfClaw.LlmProvider.Quirks.Dtos;
using SelfClaw.LlmProvider.Quirks.Implementations;
using SelfClaw.LlmProvider.Transport.Abstractions;
using SelfClaw.LlmProvider.Vendors.Dtos;

namespace SelfClaw.LlmProvider.Transport.Implementations;

/// <summary>
/// 每回合的 HTTP 管道：取调用方的共享 handler，按厂商与协议声明的 quirk 链包一层，再套一层薄
/// <see cref="HttpClient"/>。BaseAddress 与 Timeout 是每连接的，因此必须建在这里而不是共享。
/// </summary>
internal sealed class LlmHttpPipeline
{
    private readonly ILlmTransport _transport;
    private readonly LlmQuirkRegistry _quirks;

    internal LlmHttpPipeline(ILlmTransport transport, LlmQuirkRegistry quirks)
    {
        _transport = transport;
        _quirks = quirks;
    }

    internal HttpClient CreateClient(LlmConnection connection, LlmVendorDescriptor vendor, string? protocol)
    {
        var handler = BuildHandler(connection, vendor, protocol);
        return new HttpClient(handler, disposeHandler: false)
        {
            BaseAddress = connection.Endpoint,
            Timeout = connection.Timeout ?? Timeout.InfiniteTimeSpan
        };
    }

    private HttpMessageHandler BuildHandler(LlmConnection connection, LlmVendorDescriptor vendor, string? protocol)
    {
        var handler = _transport.GetHandler();
        foreach (var quirkRef in Collect(vendor, protocol))
        {
            var quirk = _quirks.GetRequired(quirkRef.Id);
            handler = quirk.Wrap(handler, new LlmQuirkContext(connection, quirkRef.Parameters ?? EmptyParameters));
        }

        return handler;
    }

    // 厂商级在前（更靠近 transport），协议级在后：越靠后的越先看到真实网络流。
    private static IEnumerable<LlmQuirkRef> Collect(LlmVendorDescriptor vendor, string? protocol)
    {
        if (vendor.Quirks is { Count: > 0 } vendorQuirks)
        {
            foreach (var quirk in vendorQuirks)
            {
                yield return quirk;
            }
        }

        if (protocol is not null &&
            vendor.ProtocolOrNull(protocol) is { Quirks: { Count: > 0 } protocolQuirks })
        {
            foreach (var quirk in protocolQuirks)
            {
                yield return quirk;
            }
        }
    }

    private static readonly IReadOnlyDictionary<string, string> EmptyParameters =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}
