using SelfClaw.LlmProvider.Chat.Implementations;
using SelfClaw.LlmProvider.Client.Abstractions;
using SelfClaw.LlmProvider.Client.Dtos;
using SelfClaw.LlmProvider.Client.Implementations;
using SelfClaw.LlmProvider.Connections.Dtos;
using SelfClaw.LlmProvider.Parameters.Dtos;
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

using System.Text.Json;

namespace SelfClaw.Tests.LlmProvider;

/// <summary>
/// 合成厂商：只用来验证机制（来源链、门控、映射、quirk 链），不冒充任何真实厂商。
/// 库本身不带厂商数据，所以这里的描述符是测试自己的夹具。
/// </summary>
internal static class TestVendors
{
    internal const string Strict = "test-strict";
    internal const string Gateway = "test-gateway";

    private const string ThinkingTypePath = "$.thinking.type";
    private const string ReasoningEffortPath = "$.reasoning_effort";
    private const string ParallelToolCallsPath = "$.parallel_tool_calls";
    private const string StorePath = "$.store";

    /// <summary>不认任何非标准字段的厂商（对应"严格端点"）。</summary>
    internal static LlmVendorDescriptor CreateStrict() => new(
        Strict,
        "Strict",
        new Uri("https://strict.test/v1/"),
        new Dictionary<string, LlmVendorProtocol>(StringComparer.Ordinal)
        {
            [LlmProtocols.OpenAiChat] = new(
            [
                new LlmVendorParameter(
                    ThinkingTypePath,
                    LlmVendorValueType.String,
                    ["option:thinking.type"],
                    GateLegacySources: true),
                new LlmVendorParameter(
                    ReasoningEffortPath,
                    LlmVendorValueType.String,
                    ["reasoning.effort", "option:reasoning_effort"],
                    GateLegacySources: true),
                new LlmVendorParameter(
                    ParallelToolCallsPath,
                    LlmVendorValueType.Boolean,
                    ["option:parallel_tool_calls"]),
                new LlmVendorParameter(
                    StorePath,
                    LlmVendorValueType.Boolean,
                    ["option:store"])
            ])
        });

    /// <summary>
    /// 会把思考开关塞进非标准字段、并且要求自定义请求头的兼容网关。
    /// 用它验证 quirk 链：库只带通用 quirk，这家网关额外声明的那个由测试自己实现。
    /// </summary>
    internal static LlmVendorDescriptor CreateGateway() => new(
        Gateway,
        "Gateway",
        new Uri("https://gateway.test/v1/"),
        new Dictionary<string, LlmVendorProtocol>(StringComparer.Ordinal)
        {
            [LlmProtocols.OpenAiChat] = new(
            [
                new LlmVendorParameter(
                    ThinkingTypePath,
                    LlmVendorValueType.String,
                    ["reasoning.effort", "option:thinking.type", "request.enableReasoning"],
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["none"] = "disabled",
                        ["false"] = "disabled",
                        ["*"] = "enabled"
                    },
                    GateLegacySources: true),
                new LlmVendorParameter(
                    ReasoningEffortPath,
                    LlmVendorValueType.String,
                    ["reasoning.effort", "option:reasoning_effort"],
                    GateLegacySources: true),
                new LlmVendorParameter(
                    ParallelToolCallsPath,
                    LlmVendorValueType.Boolean,
                    ["option:parallel_tool_calls"]),
                new LlmVendorParameter(
                    StorePath,
                    LlmVendorValueType.Boolean,
                    ["option:store"])
            ])
        },
        Quirks: [new LlmQuirkRef(TestVendorQuirk.QuirkId, TestVendorQuirk.Parameters)]);

    internal static LlmVendorCatalog CreateCatalog() =>
        new LlmVendorCatalog().Add(CreateStrict()).Add(CreateGateway());

    /// <summary>库默认的三个 quirk + 本测试自己实现的那个。</summary>
    internal static LlmQuirkRegistry CreateQuirkRegistry() =>
        LlmQuirkRegistry.CreateDefault().With(new TestVendorQuirk());

    internal static LlmClientFactory CreateFactory(ILlmTransport transport) =>
        new(CreateCatalog(), transport, quirks: CreateQuirkRegistry());

    internal static LlmConnection CreateConnection(string vendorId = Strict) => new(
        vendorId,
        new Uri("https://api.test/v1/"),
        new LlmCredential(LlmCredentialKind.Bearer, "test-key"));

    internal static LlmModel CreateModel(
        bool hasTypedConfiguration,
        string? effort = null,
        Dictionary<string, object>? options = null,
        string protocol = LlmProtocols.OpenAiChat,
        LlmModelLimits limits = default)
    {
        Dictionary<string, JsonElement>? bag = null;
        if (options is not null)
        {
            bag = options.ToDictionary(
                pair => pair.Key,
                pair => JsonSerializer.SerializeToElement(pair.Value),
                StringComparer.Ordinal);
        }

        return new LlmModel(
            "test-model",
            protocol,
            Options: bag,
            ReasoningEffort: effort,
            Limits: limits,
            HasTypedConfiguration: hasTypedConfiguration);
    }

    internal static IReadOnlyDictionary<string, JsonElement> Resolve(string vendorId, LlmModel model)
    {
        var vendor = CreateCatalog().GetRequired(vendorId);
        return new LlmOptionResolver(CreateConnection(vendorId), model)
            .Resolve(vendor.ProtocolOrNull(model.Protocol)?.Parameters);
    }
}
