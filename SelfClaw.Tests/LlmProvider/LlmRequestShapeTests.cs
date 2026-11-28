using System.Net;
using System.Text;
using SelfClaw.LlmProvider.Chat.Implementations;
using SelfClaw.LlmProvider.Client.Abstractions;
using SelfClaw.LlmProvider.Client.Dtos;
using SelfClaw.LlmProvider.Client.Implementations;
using SelfClaw.LlmProvider.Errors;
using SelfClaw.LlmProvider.Parameters.Implementations;
using SelfClaw.LlmProvider.Profiles.Dtos;
using SelfClaw.LlmProvider.Profiles.Implementations;
using SelfClaw.LlmProvider.Protocols.Abstractions;
using SelfClaw.LlmProvider.Protocols.Dtos;
using SelfClaw.LlmProvider.Protocols.Implementations;
using SelfClaw.LlmProvider.Quirks.Abstractions;
using SelfClaw.LlmProvider.Quirks.Dtos;
using SelfClaw.LlmProvider.Quirks.Implementations;
using SelfClaw.LlmProvider.Transport.Abstractions;
using SelfClaw.LlmProvider.Transport.Implementations;
using SelfClaw.LlmProvider.Vendors.Dtos;
using SelfClaw.LlmProvider.Vendors.Implementations;
using FluentAssertions;
using Microsoft.Extensions.AI;

namespace SelfClaw.Tests.LlmProvider;

/// <summary>
/// 参数表的最终形态是线上的请求体。这些断言用真实的 OpenAI SDK 序列化验证：
/// 同一份代码、同一个协议，厂商描述符不同，请求体就不同。
/// </summary>
public sealed class LlmRequestShapeTests
{
    private const string ChatCompletionBody = """
        {"id":"x","object":"chat.completion","created":0,"model":"test-model",
         "choices":[{"index":0,"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}]}
        """;

    [Fact]
    public async Task 网关厂商在没有配置时把思考字段发给端点()
    {
        var body = await SendAsync(
            TestVendors.Gateway,
            TestVendors.CreateModel(hasTypedConfiguration: false));

        body.Should().Contain("\"thinking\"");
        body.Should().Contain("\"disabled\"");
    }

    [Fact]
    public async Task 严格厂商不发这个非标准字段()
    {
        var body = await SendAsync(
            TestVendors.Strict,
            TestVendors.CreateModel(hasTypedConfiguration: false));

        body.Should().NotContain("\"thinking\"");
    }

    [Fact]
    public async Task 思考等级被翻译成推理等级()
    {
        var body = await SendAsync(
            TestVendors.Strict,
            TestVendors.CreateModel(hasTypedConfiguration: true, effort: "high"));

        body.Should().Contain("\"reasoning_effort\"");
        body.Should().Contain("\"high\"");
        body.Should().NotContain("\"thinking\"");
    }

    [Fact]
    public async Task 手写模型选项进入请求体()
    {
        var body = await SendAsync(
            TestVendors.Strict,
            TestVendors.CreateModel(
                hasTypedConfiguration: false,
                options: new() { ["store"] = true }));

        body.Should().Contain("\"store\":true");
    }

    [Fact]
    public async Task 采样与输出上限仍然由共享选项建出来()
    {
        var model = new LlmModel(
            "test-model",
            LlmProtocols.OpenAiChat,
            Sampling: new LlmSampling(Temperature: 0.3, TopP: 0.9),
            Limits: new LlmModelLimits(MaxOutputTokens: 1234));

        var body = await SendAsync(TestVendors.Strict, model);

        body.Should().Contain("\"temperature\":0.3");
        body.Should().Contain("\"top_p\":0.9");
        body.Should().Contain("1234");
    }

    [Fact]
    public async Task 调用方实现的quirk在真实请求上生效()
    {
        var handler = new CapturingHandler();
        var factory = TestVendors.CreateFactory(LlmTransports.FromHandler(handler));
        var connection = TestVendors.CreateConnection(TestVendors.Gateway);

        using var lease = factory.CreateChatClient(
            connection,
            TestVendors.CreateModel(hasTypedConfiguration: false));
        await lease.Client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], lease.Options);

        handler.RequestHeaders.Should().ContainKey(TestVendorQuirk.HeaderName)
            .WhoseValue.Should().Be(TestVendorQuirk.HeaderValue);
    }

    [Fact]
    public async Task 未注册的quirk在发请求前就失败()
    {
        var handler = new CapturingHandler();
        // 只注册库默认的那三个：网关声明的 test-vendor-marker 不在里面。
        var factory = new LlmClientFactory(
            TestVendors.CreateCatalog(),
            LlmTransports.FromHandler(handler),
            quirks: LlmQuirkRegistry.CreateDefault());

        var act = () => factory.CreateChatClient(
            TestVendors.CreateConnection(TestVendors.Gateway),
            TestVendors.CreateModel(hasTypedConfiguration: false));

        act.Should().Throw<LlmProviderException>().WithMessage($"*{TestVendorQuirk.QuirkId}*");
        handler.RequestBody.Should().BeNull();
    }

    [Fact]
    public async Task 未注册的厂商不会发出请求()
    {
        var handler = new CapturingHandler();
        var factory = TestVendors.CreateFactory(LlmTransports.FromHandler(handler));

        var act = () => factory.CreateChatClient(
            TestVendors.CreateConnection("unknown-vendor"),
            TestVendors.CreateModel(hasTypedConfiguration: false));

        act.Should().Throw<LlmVendorCatalogException>();
        handler.RequestBody.Should().BeNull();
    }

    private static async Task<string> SendAsync(string vendorId, LlmModel model)
    {
        var handler = new CapturingHandler();
        var factory = TestVendors.CreateFactory(LlmTransports.FromHandler(handler));
        var connection = TestVendors.CreateConnection(vendorId);

        using var lease = factory.CreateChatClient(connection, model);
        await lease.Client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], lease.Options);

        handler.RequestBody.Should().NotBeNull();
        return handler.RequestBody!;
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        internal string? RequestBody { get; private set; }

        internal Dictionary<string, string> RequestHeaders { get; } = new(StringComparer.OrdinalIgnoreCase);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            foreach (var header in request.Headers)
            {
                RequestHeaders[header.Key] = string.Join(",", header.Value);
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(ChatCompletionBody, Encoding.UTF8, "application/json")
            };
        }
    }
}
